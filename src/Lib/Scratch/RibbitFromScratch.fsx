// POC for ribbit, from scratch and without generics

#I __SOURCE_DIRECTORY__
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
4.) Parameters on spawned events
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
    eventDefinitions: Map<EventName, EventLogic>
    }
    with
    static member ofList vars = {
        data = Map.ofList vars
        pending = []
        log = []
        eventDefinitions = Map.empty
        }
and Expr =
    | Const of int
    | Add of Expr * Expr
    | Ref of name: VariableName
    | Prim of op: (State -> Expr)
and EventLogic =
    | Log of label: string * Expr
    | Seq of EventLogic list
    | Spawn of EventName
    | Define of name: EventName * EventLogic

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
    | Prim(op) ->
        let expr = op state
        evaluate state expr

// returns state + requirements list, but when called externally, requirements should
// always return None (i.e. dependencies should already be registered). Some requirements
// should only happen during recursion on execute.
let rec executeRecursively state event =
    let blockOn (requirements, logic) state =
        { state with pending = (requirements, logic)::state.pending }, Some requirements
    let log msg state =
        { state with log = state.log @ [msg] }, None
    match event with
    | Log (label, expr) ->
        match evaluate state expr with
        | Yield v -> log $"{label}: {v}" state
        | Require(expr', requirements) -> blockOn (requirements, Log(label, expr')) state
    | Seq events ->
        let rec loop state events =
            match events with
            | [] -> state, None
            | h::t ->
                match executeRecursively state h with
                | state, None -> loop state t
                | state, Some req -> blockOn (req, Seq t) state
        loop state events
    | Spawn eventName ->
        match state.eventDefinitions |> Map.tryFind eventName with
        | None -> log $"Logic Error! '{eventName}' is not a valid event name." state
        | Some logic -> executeRecursively state logic
    | Define(eventName, logic) ->
        { state with eventDefinitions = state.eventDefinitions |> Map.add eventName logic }, None

type Msg =
    | Supply of VariableName * int
    | Eval of label: string * Expr

let update msg state =
    match msg with
    | Eval(label, expr) ->
        executeRecursively state (Log (label, expr)) |> fst
    | Supply(name, v) ->
        let unblock var v ((vars, expr): (VariableName list * EventLogic) as pendingRow) =
            if vars |> List.exists ((=) var) then
                (vars |> List.filter ((<>) var)), expr // no longer waiting for var because it's here
            else
                pendingRow        
        let pending = state.pending |> List.map (unblock name v)
        // now there might be unblocked rows
        let unblocked, blocked = pending |> List.partition (fun (vars, _) -> List.isEmpty vars)
        let progress state logic =
            match executeRecursively state logic with
            | state, None -> state
            | state, Some req -> { state with pending = (req, logic)::state.pending }
        unblocked |> List.fold (fun state (_, event) -> progress state event) { state with pending = blocked; data = state.data |> Map.add name v }

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
let d6 = Prim(fun state -> Const (rand 6))

eval "3+7-5+x" (Add(Add(Add(Const 3, Const 7), Const -5), Ref "x"))
eval "3+y-5+x" (Add(Add(Add(Const 3, Ref "y"), Const -5), Ref "x"))
supply "y" -10
supply "x" 12
reset()
eval "2d6+y" (Add(Add(d6, d6), Ref "y"))
supply "y" 3
