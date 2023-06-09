// POC for ribbit, from scratch and without generics

#I __SOURCE_DIRECTORY__
#I "."
#I ".."
#load "GameLoop.fsx"

// do we need HOAS in this case? Only if we want eval to have typed output, I think, like eval<int> vs. eval<int -> int>.
// for now let's do FOAS and see how far we get.

(*

Features of POC:

1.) Lazy data entry (done)
2.) Expressions vs. Events. Expressions not logged directly,
    do not directly modify state but only request modifications
    (like event spawning or data requests), can return revised
    expression (event definition vs. event awaiting)).
    Initially just one event: Log(expr). (done)
3.) Event Spawning (Events triggered by Events instead of directly by Cmd).
    Implies other things too, like Statements within events, or let's call
    EventLogic. And that means that Log is an EventLogic. (done)
4.) Parameters on spawned events (design work in progress)
5.) Implicit spawning/event triggering
6.) Filter expressions on event triggers
7.) Variable scoping with inheritance
8.) Variable defaults (support primitives/external function calls)
9.) Delta values ("subtract 2 from inherited value")
10.) Temporal scopes ("this round", "until spell XYZ expires", etc.)
11.) Show log hierarchy (interactive explore)
12.) AI, Behaviors

Scenario:
3 orcs vs. orog
Needs rules for HP, damage, attacks, initiative

*)

type VariableName = string
type EventName = string
type Label = Label of string
type Requirements = VariableName list
type Result<'expr, 'result> =
    | Yield of 'result
    | Require of revised: 'expr * Requirements

type State = {
    data: Map<string, int>
    pending: (VariableName list * EventLogic) list
    log: string list
    eventDefinitions: Map<EventName, EventDefinition>
    }
    with
    static member ofList vars = {
        data = Map.ofList vars
        pending = []
        log = []
        eventDefinitions = Map.empty
        }
and Value = int
and Expr =
    | Const of Value
    | Add of Expr * Expr
    | Ref of name: VariableName
    | Inherent of params': Expr list * op: (Value list -> State -> Value)
and Condition = Condition of Expr * ((Value -> bool) option)
and EventDefinition = {
    name: EventName
    parameters: Parameter list
    body: (Argument list -> EventLogic)
    }
and EventLogic =
    | Log of label: string * Expr
    | Seq of EventLogic list
    | Spawn of EventName
    | Define of EventDefinition
    | Set of name: VariableName * Expr
    | If of Condition * EventLogic * EventLogic option
and Parameter =
    | ParamExpr of Expr
    | ParamName of VariableName
and Argument =
    | ArgValue of Value
    | ArgName of VariableName

let rec evaluate state = function
    | Const n -> Yield n
    | Add(lhs, rhs) ->
        let (|ExprOf|) = function
            | Require (expr, req) -> expr, req
            | Yield v -> Const v, []
        match evaluate state lhs, evaluate state rhs with
        | Yield l, Yield r -> Yield (l + r)
        | ExprOf(lexpr, lreq), ExprOf(rexpr, rreq) ->
            Require(Add(lexpr, rexpr),
                match lreq, rreq with
                | [], lst | lst, [] -> lst
                | _ -> lreq@rreq |> List.distinct)
    | Ref name as expr ->
        match state.data |> Map.tryFind name with
        | Some v -> Yield v
        | None -> Require(expr, [name])
    | Inherent(params', op) ->
        let rec recur argsR = function
            | [] -> // no more parameters, arguments are ready
                let args = List.rev argsR
                op args state |> Yield
            | hparam::rest ->
                match evaluate state hparam with
                | Yield v -> recur (v::argsR) rest
                | Require(expr, reqs) ->
                    let args = List.rev argsR
                    let op' args' state' =
                        op (args@args') state'
                    Require(Inherent(rest, op'), reqs)
        recur [] params'

let rec set (name:VariableName, v) state =
    let unblock var v ((vars, expr): (VariableName list * EventLogic) as pendingRow) =
        if vars |> List.exists ((=) var) then
            (vars |> List.filter ((<>) var)), expr // no longer waiting for var because it's here
        else
            pendingRow
    let pending = state.pending |> List.map (unblock name v)
    // now there might be unblocked rows
    let unblocked, blocked = pending |> List.partition (fun (vars, _) -> List.isEmpty vars)
    unblocked |> List.fold (fun state (_, event) -> execute state event) { state with pending = blocked; data = state.data |> Map.add name v }
// returns a new state where event is either executed or added to pending list, with dependencies
and execute state event =
    let blockOn (requirements: Requirements, logic) state =
        { state with pending = (requirements, logic)::state.pending }
    let log msg state =
        { state with log = state.log @ [msg] }, None
    let rec recur state = function
        | Log (label, expr) ->
            match evaluate state expr with
            | Yield v -> log $"{label}: {v}" state
            | Require(expr', requirements) -> state, Some(requirements, Log(label, expr'))
        | Seq events ->
            let rec loop (state: State) = function
                | [] -> state, None
                | h::t ->
                    match recur state h with
                    | state, None -> loop state t
                    | state, Some(reqs, h') ->
                        state, Some (reqs, Seq (h'::t))
            loop state events
        | Spawn eventName ->
            match state.eventDefinitions |> Map.tryFind eventName with
            | None -> log $"Logic Error! '{eventName}' is not a valid event name." state
            | Some def ->
                let logic: EventLogic =
                    match def.parameters with
                    | [] -> def.body []
                    | parameters -> notImpl() // how to evaluate parameters? What if not all the expressions are ready yet, where to store that? In pending?
                recur state logic
        | Define(eventDefinition) ->
            { state with eventDefinitions = state.eventDefinitions |> Map.add eventDefinition.name eventDefinition }, None
        | Set(name, expr) ->
            match evaluate state expr with
            | Yield v ->
                set (name, v) state, None
            | Require(expr', requirements) -> state, Some(requirements, Set(name, expr'))
        | If(Condition(expr, transform), then', else') ->
            match evaluate state expr with
            | Require(expr', reqs) ->
                state, Some(reqs, If(Condition(expr', transform), then', else'))
            | Yield v ->
                match transform with
                | None when v <> 0 -> recur state then'
                | Some t when t v -> recur state then'
                | _ ->
                    match else' with
                    | Some logic -> recur state logic
                    | None -> state, None
    match recur state event with
    | state, None -> state
    | state, Some(reqs, logic) -> blockOn (reqs, logic) state

type Msg =
    | Supply of VariableName * int
    | Eval of label: string * Expr
    | Exec of EventLogic

let update msg state =
    match msg with
    | Eval(label, expr) ->
        execute state (Log (label, expr))
    | Exec logic -> execute state logic
    | Supply(name, v) -> execute state (Set(name, Const v))

let view state =
    for entry in state.log do
        printfn "%s" entry
    let mutable state = state
    for (vars, _) in state.pending do
        for v in vars do
            printfn $"Enter a value for {v}"
let mutable game = State.ofList ["x", 10]
let reset() = game <- State.ofList []
let exec msg = GameLoop.gameIter &game view (update msg)
let eval label expr = exec (Eval(label, expr))
let supply name v = exec (Supply(name, v))
let d6 = Inherent([], fun _ _ -> (rand 6))
let define name logic = exec (Exec (Define({ name = name; parameters = []; body = fun _ -> logic })))
let spawn name = exec (Exec (Spawn(name)))

eval "3+7-5+x" (Add(Add(Add(Const 3, Const 7), Const -5), Ref "x"))
eval "3+y-5+x" (Add(Add(Add(Const 3, Ref "y"), Const -5), Ref "x"))
supply "y" -10
supply "x" 12
reset()
eval "2d6+y" (Add(Add(d6, d6), Ref "y"))
supply "y" 3
exec (Exec(Seq[Log("d6", d6); Set("z", d6); Log("3+7-5+z", Add(Add(Add(Const 3, Const 7), Const -5), Ref "z")) ]))
define "halve x" <|
    If(Condition(Ref "x", Some (flip (>=) 2)),
        Seq [
            Log("x was", Ref "x")
            Set("x", Inherent([Ref "x"], fun [x] _state -> x / 2))
            Log("x is now", Ref "x")
        ],
        Some (Log ("x is already small enough", Ref "x")))
define "halve z" <|
    Set ("z", Inherent([Ref "z"], fun [z] _ -> z / 2))
define "reduce z" <|
    If(Condition(Ref "z", None),
        Seq [
        Spawn "halve z"
        Set ("z", Inherent([Ref "z"; d6], fun [z; roll] _ -> z + roll))
        Log("z", Ref "z")
        ], None)
exec (Exec(Spawn("halve x")))
exec (Exec(Spawn("halve x")))
supply "x" 120
exec (Exec(Spawn("halve x")))
exec (Exec(Spawn("halve x")))
exec (Exec(Spawn("halve x")))
spawn "reduce z"
supply "z" 100
game
