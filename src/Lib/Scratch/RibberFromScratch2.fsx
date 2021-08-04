module Rewrite

#I "."
#I ".."
#load "Optics.fs"
#load "Common.fs"
open Optics
open type Optics.Operations
(*
Concepts: Expression eval

*)

type RuntimeValue = String of string | Number of int | Boolean of bool | Undefined
type AgentId = int
type EventId = int
type PropertyName = string
type VariableReference = DataRef of agent: AgentId * property: PropertyName | LocalRef of guid: string | EventRef of event:EventId

type Expression =
    | Const of RuntimeValue
    | BinaryOp of Expression * Expression * BinaryOperator
    | Conditional of test: Expression * andThen: Expression * orElse: Expression option
    | Dereference of VariableReference
    | Roll of n:int * dSize: int * plus: int
    | StartEvent of eventName: string * args: (PropertyName * Expression) list

and BinaryOperator =
    | Plus
    | Minus
    | Times
    | Divide
    | Equals
    | AtLeast
    | AtMost

type Statement =
    | Return of Expression
    | Assign of VariableReference * Expression
    | Sequence of Statement list
type CurrentExpressionValue = Result<RuntimeValue, VariableReference list>

type ExecutionContext =
    {
    workQueue: EventId list // LIFO queue for ease of implementation but it probably doesn't matter what the order is
    currentEvent: EventId option
    }

type DereferenceF<'state> = ExecutionContext -> VariableReference -> 'state -> CurrentExpressionValue
type DeferF<'state> = EventId -> Statement list -> VariableReference -> 'state -> 'state

type CompleteF<'state> = EventId -> RuntimeValue -> 'state -> 'state
type SupplyF<'state> = ExecutionContext -> VariableReference -> RuntimeValue -> 'state -> ('state * EventId list)
type ResumeF<'state> = EventId -> 'state -> Statement list

let evaluate
    (api: {|
            dereference: DereferenceF<'state>
        |})
    (ctx: ExecutionContext)
    (state: 'state)
    expr
    : CurrentExpressionValue =
        let rec eval : Expression -> CurrentExpressionValue=
            function
            | Const v -> Ok v
            | BinaryOp(lhs, rhs, op) ->
                match eval lhs, eval rhs with
                | Ok lhs, Ok rhs ->
                    let binary = function
                        | String lhs, String rhs, Plus -> lhs + rhs |> String
                        | Number lhs, Number rhs, Plus -> lhs + rhs |> Number
                        | Boolean lhs, Boolean rhs, Plus -> (lhs || rhs) |> Boolean
                        | Number lhs, Number rhs, Minus -> lhs - rhs |> Number
                        | Number lhs, Number rhs, Times -> lhs * rhs |> Number
                        | Boolean lhs, Boolean rhs, Times -> (lhs && rhs) |> Boolean
                        | Number lhs, Number rhs, Divide -> lhs / rhs |> Number
                        | Number lhs, Number rhs, Equals -> (lhs = rhs) |> Boolean
                        | String lhs, String rhs, Equals -> (lhs = rhs) |> Boolean
                        | Boolean lhs, Boolean rhs, Equals -> lhs = rhs |> Boolean
                        | Number lhs, Number rhs, AtLeast -> lhs >= rhs |> Boolean
                        | Number lhs, Number rhs, AtMost -> lhs <= rhs |> Boolean
                        | _ -> Undefined
                    binary(lhs, rhs, op) |> Ok
                | Error _ as lhs, Ok _ -> lhs
                | Ok _, (Error _ as rhs) -> rhs
                | Error lhs, Error rhs -> Error (lhs @ rhs)
            | Conditional(test, andThen, orElse) ->
                match eval test with
                | Ok (Boolean true) -> eval andThen
                | Ok (Boolean false) ->
                    match orElse with
                    | Some expr -> eval expr
                    | _ -> Ok Undefined
                | Ok _ -> Ok Undefined
                | err -> err
            | Dereference ref ->
                api.dereference ctx ref state
            | Roll _ | StartEvent _ ->
                // By evaluation time, Roll and StartEvent should have already been rewritten to Dereference expressions
                Ok Undefined 
        eval expr

let execute
    (api: {|
            dereference: DereferenceF<'state>
            supply: SupplyF<'state>
        |})
    (ctx: ExecutionContext)
    (state: 'state)
    (statements: Statement list)
        : 'state * Statement list * CurrentExpressionValue * ExecutionContext =
        let evalApi = {| dereference = api.dereference |}
        let rec loop state ctx = function
            | current::rest as stack ->
                match current with
                | Return expr ->
                    match evaluate evalApi ctx state expr with
                    | Ok v -> state, [], Ok v, ctx
                    | awaiting -> state, [current], awaiting, ctx
                | Assign(ref, expr) ->
                    match evaluate evalApi ctx state expr with
                    | Ok v ->
                        let state, unblocked = api.supply ctx ref v state                        
                        let ctx = { ctx with workQueue = unblocked @ ctx.workQueue }
                        loop state ctx rest
                    | awaiting -> state, stack, awaiting, ctx
                | Sequence statements ->
                    // unpack the statements onto the head of the instruction stack
                    loop state ctx (statements @ rest)
            | [] ->
                state, [], Ok Undefined, ctx // returning a value from an event is optional, could just be assignments
        loop state ctx statements

let progressToFixedPoint
    (api: {|
             dereference: DereferenceF<'state>
             defer: DeferF<'state>
             resume: ResumeF<'state>
             supply: SupplyF<'state>
        |})
    (state: 'state, ctx: ExecutionContext) =
        let mutable queue = ctx.workQueue
        let mutable state = state
        while queue.IsEmpty |> not do
            match queue with
            | currentEvent::rest ->
                queue <- rest
                let statements = api.resume currentEvent state
                match execute
                        {| dereference = api.dereference; supply = api.supply |}
                        { currentEvent = Some currentEvent; workQueue = [] }
                        state
                        statements
                      with
                | state', _, Ok v, ctx ->
                    match api.supply { ExecutionContext.currentEvent = Some currentEvent; ExecutionContext.workQueue = [] } (EventRef currentEvent) v state' with
                    | state', unblockedWorkItems ->
                        state <- state'
                        queue <- ctx.workQueue @ unblockedWorkItems @ queue
                | state', statements, Error dependencies, ctx ->
                    let mutable state'' = state'
                    for d in dependencies do
                        state'' <- api.defer currentEvent statements d state''
                    state <- state''
            | _ -> ()
        state

let query
    (api: {|
            placeholder: 'state -> VariableReference list
        |})
    (state: 'state) =
        notImpl()

let supply
    (api: {|
            data_: (AgentId * PropertyName) -> Lens<'state, RuntimeValue>
            query: 'state -> VariableReference list
        |})
    (agent: AgentId, property: PropertyName)
    (v: RuntimeValue)
    (state: 'state)
    =
        let satisfies = api.query state
        let lens = api.data_ (agent, property)
        let state = state |> write lens v
        state

type Scope = {
    properties: Map<PropertyName, RuntimeValue>
    }
type Event = EventResult of RuntimeValue | EventState of EventState
and EventState = { scope: Scope; instructionStack: Statement list; dependencies: Set<VariableReference> }

type Game = {
    roster: Map<string, AgentId list>
    rosterReverse: Map<AgentId, string>
    data: Map<AgentId, Scope>
    events: Map<EventId, Event>
    nextEventId: EventId // an id generator
    dependencies: Map<VariableReference, EventId list> // to support execution chaining
    dataDependencies: (AgentId*PropertyName) list // for display in UI. Local variables and event results can't be input by the user so don't need to go in this list.
    }
    with
    static member fresh = {
        roster = Map.empty
        rosterReverse = Map.empty
        data = Map.empty
        events = Map.empty
        nextEventId = 1
        dependencies = Map.empty
        dataDependencies = []
        }
    static member start instructions (g:Game) =
        let eventId, g = g.nextEventId, { g with nextEventId = g.nextEventId + 1 }
        let g =
            {
                g with
                    events = g.events |> Map.change eventId (function
                        | None -> Some(EventState { scope = { properties = Map.empty }; instructionStack = instructions; dependencies = Set.empty })
                        | _ -> shouldntHappen()
                        )
            }
        let g = progressToFixedPoint
                    {| dereference = Game.dereference; defer = Game.defer; resume = Game.resume; supply = Game.supply |}
                    (g, { workQueue = [eventId]; currentEvent = Some eventId })
        eventId, g
    static member defer eventId statements ref (g:Game) =
        {
            g with
                events = g.events |> Map.change eventId (function
                    | Some(EventState(state)) -> Some(EventState { state with dependencies = state.dependencies |> Set.add ref; instructionStack = statements })
                    | _ -> shouldntHappen()
                    )
                dependencies = g.dependencies |> Map.change ref (function
                    | Some(lst) -> eventId::lst |> Some
                    | None -> Some [eventId]
                    )
                dataDependencies =
                    // add to dataDependencies if this is a new dependency, so it can be added to the UI
                    match ref with
                    | DataRef(agentId, propertyName) ->
                        if g.dependencies |> Map.containsKey ref then g.dataDependencies
                        else g.dataDependencies @ [agentId, propertyName]
                    | _ -> g.dataDependencies
        }
    static member set (ctx: ExecutionContext) (ref:VariableReference) (value:RuntimeValue) (g:Game) =
        match ref with
        | DataRef(agentId, propName) ->
            let data =
                g.data |> Map.change agentId (function
                | None -> { properties = Map.ofList [propName, value] } |> Some
                | Some scope -> { scope with properties = scope.properties |> Map.add propName value } |> Some)
            { g with data = data }
        | LocalRef(propName) ->
            let events =
                match ctx.currentEvent with
                | None -> shouldntHappen()
                | Some eventId ->
                    g.events |> Map.change eventId (function
                    | Some (EventState state) ->
                        let scope = { state.scope with properties = state.scope.properties |> Map.add propName value }
                        Some (EventState { state with scope = scope })
                    | _ -> shouldntHappen()
                    )
            { g with events = events }
            
        | EventRef(eventId) ->
            let events =
                g.events |> Map.change eventId (function
                | Some (EventState state) ->
                    Some (EventResult value)
                | _ -> shouldntHappen()
                )
            { g with events = events }

    static member supply (ctx: ExecutionContext) (ref:VariableReference) (value:RuntimeValue) (g:Game) =
        match g.dependencies |> Map.tryFind ref with
        | None ->
            Game.set ctx ref value g, []
        | Some dependencies ->
            let g =
                match ref with
                | DataRef(agentId, propName) ->
                    let dd = g.dataDependencies |> List.filter ((<>) (agentId, propName))
                    { g with dataDependencies = dd; dependencies = g.dependencies |> Map.remove ref }
                | _ -> { g with dependencies = g.dependencies |> Map.remove ref }
            Game.set ctx ref value g, dependencies
    static member resume (eventId: EventId) (g:Game) =
        match g.events |> Map.tryFind eventId with
        | Some (EventState state) ->
            state.instructionStack
        | _ -> shouldntHappen()
    static member dereference (ctx: ExecutionContext) (ref:VariableReference) (g:Game) =
        match ref with
        | DataRef(agentId, propName) ->
            match g.data |> Map.tryFind agentId with
            | Some scope ->
                match scope.properties |> Map.tryFind propName with
                | Some v -> Ok v
                | _ -> Error [ref]
            | _ -> Error [ref]
        | LocalRef guid ->
            match ctx.currentEvent with
            | None -> shouldntHappen()
            | Some eventId ->
                match g.events |> Map.tryFind eventId with
                | Some (EventState state) ->
                    match state.scope.properties |> Map.tryFind guid with
                    | Some v -> Ok v
                    | _ -> Error [ref]
                | _ -> shouldntHappen()
        | EventRef eventId ->
            match ctx.currentEvent with
            | None -> shouldntHappen()
            | Some eventId ->
                match g.events |> Map.tryFind eventId with
                | Some (EventState state) ->
                    Error [ref]
                | Some (EventResult v) -> Ok v
                | None -> shouldntHappen()

let foo(game: Game, id: EventId, ref:VariableReference) =
    progressToFixedPoint {| dereference = Game.dereference; defer = Game.defer; resume = Game.resume; supply = Game.supply |} (game, { workQueue = []; currentEvent = None })

let g = Game.fresh
let eventId, g1 =
    Game.start [
    Assign(LocalRef "abc", BinaryOp(Const(Number 1), Const(Number 4), Plus));
    Return (Dereference (LocalRef "abc"))
    ] g
g1.events.[eventId]
