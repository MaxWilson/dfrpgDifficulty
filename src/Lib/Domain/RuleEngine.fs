namespace Domain
module RuleEngine =
    open Domain.Model
    open Domain.Model.Ribbit
    open AutoGen
    open Common
    open type Common.Ops

    module Logic =
        // Like Task.Continue or Async.Map
        let continueWith f (logic: HOASLogic<_,_,_>) =
            let rec continueWith logic state =
                match logic state with
                | state, Ready v ->
                    f v state
                | state, Awaiting(demand, logic) ->
                    state, Awaiting (demand, logic |> continueWith)
            continueWith logic

        let tryRead id (prop: Prop<obj, 't>) (state: State) =
            match state.data |> Map.tryFind (id, prop.name) with
            | Some (:? 't as v) -> Some v
            | _ -> None

        let defineAffordance name props logic state =
            state

        let demand (id, propName as key) logic state =
            match state.outstandingQueries |> Map.tryFind key with
            | None ->
                { state with outstandingQueries = addTo state.outstandingQueries (id, propName) [logic] }
            | Some current ->
                { state with outstandingQueries = addTo state.outstandingQueries (id, propName) (current@[logic]) }

        let addToQueue logic state =
            { state with workQueue = add(logic, state.workQueue) }

        let fulfill (id, prop: Prop<obj, 't>) (value: 't) state =
            let state = { state with data = addTo state.data (id, prop.name) (box value) }
            match state.outstandingQueries |> Map.tryFind (id, prop.name) with
            | None | Some [] -> state
            | Some unblocked ->
                unblocked |> List.fold (flip addToQueue) ({ state with outstandingQueries = state.outstandingQueries |> Map.remove (id, prop.name) })

        let andLog id logic =
            logic |> continueWith (fun msg state -> { state with settled = addTo state.settled id msg; log = addTo state.log id }, Ready ())

        let processLogic = function
            | state, Ready () ->
                state
            | state, Awaiting(Some demand', logic) ->
                state |> demand demand' (HOAS logic)
            | state, Awaiting(None, logic) ->
                state |> addToQueue (HOAS logic)

        let spawn (logic: Logic<string>) state =
            let id, state = state |> IdGenerator.newId State.ids_
            match logic with
            | HOAS logic ->
                (logic |> andLog id) state |> processLogic
            | FOAS -> notImpl()

        let triggerAffordance name propValues state =
            state

        let rec untilFixedPoint state =
            let queue = state.workQueue
            let state = { state with workQueue = Queue.empty }
            match queue |> List.fold (fun state -> function HOAS logic -> logic state |> processLogic | FOAS -> notImpl()) state with
            | { workQueue = [] } as state -> state
            | state -> state |> untilFixedPoint

        module Builder =
            type Builder<'state,'demand>() =
                member _.Return x =
                    fun state -> state, Ready x
                member _.ReturnFrom logic = logic
                member _.Bind (logic: HOASLogic<'state, 'demand, 't>, rhs: 't -> HOASLogic<'state, 'demand, 'r>) : HOASLogic<'state, 'demand, 'r> =
                    continueWith rhs logic
                member _.Run x = HOAS x

            let rec read id prop state =
                match tryRead id prop state with
                | Some v -> state, Ready v
                | None ->
                    state, Awaiting(Some (id, prop.name), read id prop)
            let logic = Builder<State, Demand>()