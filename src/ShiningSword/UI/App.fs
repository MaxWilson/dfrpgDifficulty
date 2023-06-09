module UI.App

open Fable.Core.JsInterop
open Elmish
open Elmish.React
open Feliz
open Konva

// make sure errors are not silent: show them as Alerts (ugly but better than nothing for now)
open Fable.Core.JsInterop
open Fable.Core

importSideEffects "../sass/main.sass"

module App =
    open Domain.Character
    open Domain.Character.Universal
    open UI.Chargen.Interaction

    type Page =
        | Home
        | Generate of Chargen.View.Model
        | Adventure of Adventure.Model
    type Model = { current: Page; currentUrl: string option; error: string option; roster: CharacterSheet array; graveyard: CharacterSheet array }
    type Msg =
        | Error of msg: string
        | ClearError
        | Transform of (Model -> Model)
        | ChargenMsg of Chargen.View.Msg
        | AdventureMsg of Adventure.Msg
        | Open of Page * url: string option
        | GoHome
        | Navigate of url: string
        | UpdateUrl of url: string // change URL without adding it to history--use this when a user would be surprised/irritated if "back button" acted as "undo"
        | AddOrUpdateRoster of CharacterSheet * stillAlive: bool
        | ResumePlay of id: int
        | ClearRoster
        | ClearGraveyard
        | DeleteCharacter of id: int

    let init initialCmd =
        { current = Page.Home; currentUrl = None; error = None; roster = LocalStorage.PCs.read(); graveyard = LocalStorage.Graveyard.read() }, Cmd.batch initialCmd
    let chargenControl dispatch = function
        | Chargen.View.SaveAndQuit character ->
            dispatch (AddOrUpdateRoster (character, true))
            dispatch GoHome
        | Chargen.View.Cancel -> dispatch GoHome
        | Chargen.View.UpdateUrl suffix ->
            UpdateUrl $"chargen/{suffix}"
            |> dispatch
        | Chargen.View.BeginAdventuring character ->
            Transform (fun m -> { m with current = Page.Adventure (Adventure.init character) }) |> dispatch
            UpdateUrl "/" |> dispatch // TODO: make a URL router directly to character's adventure
    let update msg model =
        let model = { model with error = None } // clear error message whenever a new command is received
        match msg with
        | Error msg ->
            // print to console as well as screen just in case somehow another message prevents us from seeing this error
            // consider using Browser.window.alert instead.
            printfn $"\n============================\n=========ERROR==============\n{msg}\n============================\n\n"
            { model with error = Some msg }, Cmd.Empty
        | ClearError ->
            { model with error = None }, Cmd.Empty
        | Transform f -> { f model with error = None }, Cmd.Empty
        | Navigate url ->
            // if we send a command like OpenPage that sends a Navigate message, we don't want
            // that to trigger the re-loading logic in Url.parse which will re-send the original
            // message and mess up our browser history
            if model.currentUrl <> Some url then
                { model with currentUrl = Some url }, Navigation.Navigation.newUrl ("#" + url)
            else
                model, Cmd.Empty
        | UpdateUrl url ->
            // if we send a command like OpenPage that sends a Navigate message, we don't want
            // that to trigger the re-loading logic in Url.parse which will re-send the original
            // message and make us do unnecessary work
            if model.currentUrl <> Some url then
                { model with currentUrl = Some url }, Navigation.Navigation.modifyUrl ("#" + url)
            else
                model, Cmd.Empty
        | AddOrUpdateRoster (characterSheet, stillAlive) ->
            // helper method for working with universal sheets
            let getId (sheet1: CharacterSheet) = sheet1.converge((fun c -> c.id), (fun c -> c.id), (fun c -> c.id))
            let delete id' collection = collection |> Array.filter (fun r -> getId r <> id')
            let addOrUpdate collection =
                // assign a unique id if one isn't already there
                let id', sheet =
                    match getId characterSheet with
                    | Some id -> Some id, characterSheet
                    | None ->
                        let id' = (Array.append model.roster model.graveyard) |> Array.map (getId >> Option.get) |> Array.fold max 0 |> (+) 1 |> Some
                        id', characterSheet.map2e(fun c -> { c with id = id' }).map5e(fun c -> { c with id = id' }).mapDF(fun c -> { c with id = id' })

                // recent updated entries should be at the head of the list, so filter and then re-add at front
                Array.append [|sheet|] (delete id' collection)

            if stillAlive then
                let roster' = addOrUpdate model.roster
                LocalStorage.PCs.write roster'
                { model with roster = roster' }, Cmd.Empty
            else
                let graveyard' = addOrUpdate model.graveyard
                let id = getId characterSheet
                let roster' = model.roster |> delete id
                LocalStorage.PCs.write roster'
                LocalStorage.Graveyard.write graveyard'
                { model with roster = roster'; graveyard = graveyard' }, Cmd.Empty

        | ClearRoster ->
            let roster = Array.empty
            LocalStorage.PCs.write roster
            { model with roster = roster }, Cmd.Empty
        | ClearGraveyard ->
            let graveyard = Array.empty
            LocalStorage.Graveyard.write graveyard
            { model with graveyard = graveyard }, Cmd.Empty
        | DeleteCharacter id ->
            let roster = model.roster |> Array.filter (function Detail2e char -> char.id <> Some id | Detail5e char -> char.id <> Some id | DetailDF char -> char.id <> Some id )
            LocalStorage.PCs.write roster
            let graveyard = model.graveyard |> Array.filter (function Detail2e char -> char.id <> Some id | Detail5e char -> char.id <> Some id | DetailDF char -> char.id <> Some id )
            LocalStorage.Graveyard.write graveyard
            { model with roster = roster; graveyard = graveyard }, Cmd.Empty
        | GoHome ->
            { model with current = Home; error = None }, Navigate "/" |> Cmd.ofMsg
        | Open(page, Some url) -> { model with current = page}, Navigate url |> Cmd.ofMsg
        | Open(page, None) -> { model with current = page}, Cmd.Empty
        | ChargenMsg msg ->
            match model.current with
            | (Page.Generate chargenModel) ->
                let cmd = (ChargenMsg >> Cmd.ofMsg)
                let chargenModel, cmd = Chargen.View.update (ChargenMsg >> Cmd.ofMsg) (fun msg -> [fun dispatch -> chargenControl dispatch msg]) msg chargenModel
                { model with current = (Page.Generate chargenModel)}, cmd
            | _ -> model, (Error $"Message '{msg}' not compatible with current page ({model.current}))" |> Cmd.ofMsg)
        | AdventureMsg msg ->
            match model.current with
            | (Page.Adventure model') ->
                { model with current = (Page.Adventure (Adventure.update msg model'))}, Cmd.Empty
            | _ -> model, (Error $"Message '{msg}' not compatible with current page ({model.current}))" |> Cmd.ofMsg)
        | ResumePlay id ->
            match model.roster |> Array.tryFind (function Detail2e c -> c.id = Some id | Detail5e c -> c.id = Some id | DetailDF char -> char.id = Some id ) with
            | Some character ->
                model, Open(Page.Adventure (Adventure.init character), Some $"resume/{id}") |> Cmd.ofMsg
            | _ -> model, Error $"There is no character with id #{id}" |> Cmd.ofMsg

    open Feliz.Router
    let view (model: Model) dispatch =
        let class' element (className: string) (children: ReactElement list) =
            element [prop.className className; prop.children children]

        match model.current with
        | _ when model.error.IsSome ->
            class' Html.div "errorMsg" [
                Html.div [Html.text model.error.Value]
                Html.button [prop.text "OK"; prop.onClick (thunk1 dispatch ClearError)]
                Html.button [prop.text "Start over"; prop.onClick (thunk1 dispatch GoHome)]
                ]
        | Page.Generate model ->
            Chargen.View.view model (chargenControl dispatch) (ChargenMsg >> dispatch)
        | Page.Adventure adventure ->
            let stillAlive = Adventure.stillAlive (adventure.state.ribbit)
            let control = function
            | Adventure.Save ->
                // avoid saving unless an ID has already been assigned, partly to avoid duplications (because of different Ids)
                // and partly because the player might not be ready to keep the character.
                let hasAlreadyBeenSaved (char:CharacterSheet) = char.converge((fun c -> c.id.IsSome), (fun c -> c.id.IsSome), (fun c -> c.id.IsSome))
                for char in adventure.state.mainCharacter::adventure.state.allies |> List.filter hasAlreadyBeenSaved |> List.rev do
                    (char, stillAlive char) |> AddOrUpdateRoster |> dispatch
            | Adventure.SaveAndQuit ->
                for char in adventure.state.mainCharacter::adventure.state.allies |> List.rev do
                    (char, stillAlive char) |> AddOrUpdateRoster |> dispatch
                GoHome |> dispatch
            | Adventure.Error msg ->
                Error msg |> dispatch
            UI.Adventure.view adventure control (AdventureMsg >> dispatch)
        | Home ->
            Html.div [
                prop.className "homePage"
                prop.children [
                    Html.div [
                        prop.children [
                            Html.text (match model.error with Some msg -> msg | None -> "Welcome to Shining Sword")
                            ]
                        prop.style [style.marginBottom 10]
                        ]
                    Html.button [
                        prop.text "Create a character"
                        // "remember" the user's ruleset preference
                        prop.onClick(fun _ ->
                            match model.roster |> Array.tryHead with
                            | None | Some (Detail2e _) ->
                                Open(Page.Generate (Chargen.View.init()), Some "chargen/adnd") |> dispatch
                            | Some (Detail5e _)->
                                Open(Page.Generate (Chargen.View.init()), Some "chargen/5e") |> dispatch
                                ChargenMsg(Chargen.View.SetRuleset Chargen.View.WotC) |> dispatch
                            | Some (DetailDF _)->
                                Open(Page.Generate (Chargen.View.init()), Some "chargen/df") |> dispatch
                                ChargenMsg(Chargen.View.SetRuleset Chargen.View.Ruleset.DungeonFantasy) |> dispatch
                            )
                        ]
                    class' Html.div "growToFill" [
                        let render stillAlive (ch: CharacterSheet) =
                            let txt, id, flair, cssClass =
                                match ch with
                                | Detail2e char ->
                                    char.name, char.id, "AD&D", "flair ADND"
                                | Detail5e char ->
                                    char.name, char.id, "D&D 5E", "flair DND5e"
                                | DetailDF char ->
                                    char.name, char.id, "DFRPG", "flair DF"
                            [
                                Html.span [
                                    prop.text flair
                                    prop.className cssClass
                                    ]
                                Html.span [prop.text txt; prop.className "characterName"; prop.onClick (thunk1 dispatch (ResumePlay id.Value))]
                                if stillAlive then
                                    Html.button [
                                        prop.text $"Resume"
                                        prop.className "resumeCommand"
                                        prop.onClick (thunk1 dispatch (ResumePlay id.Value))
                                        ]
                                else
                                    // placeholder to make the grids come out right
                                    Html.div[prop.text ""]
                                Html.button [
                                    prop.text $"Delete"
                                    prop.className "deleteCommand"
                                    prop.onClick (thunk1 dispatch (DeleteCharacter id.Value))
                                    ]
                                ]

                        class' Html.div "existingCharacters" [
                            for ch in model.roster do
                                yield! render true ch

                            if model.roster.Length > 0 then
                                Html.button [prop.text "Delete all characters"; prop.className "deleteAllCommand"; prop.onClick (thunk1 dispatch ClearRoster)]
                            ]
                        if(model.graveyard.Length > 0) then
                            Html.h2 "The honored dead"
                            class' Html.div "graveyard" [
                                for ch in model.graveyard do
                                    yield! render false ch
                                Html.button [prop.text "Delete entire graveyard"; prop.className "deleteAllCommand"; prop.onClick (thunk1 dispatch ClearGraveyard)]
                                ]
                        ]

                    Html.div [
                        prop.className "footer"
                        prop.children [
                            Html.a [prop.href "https://www.flaticon.com/free-icons/sword"; prop.text "Sword icon created by pongsakornRed - Flaticon"]
                            ]
                        ]
                    ]
               ]

module Url =
    open Chargen.View
    open App
    module Parse =
        open Browser.Types
        open Packrat
        let locationParser (rootActivePattern: ParseRule<_>) (loc: Location) =
            let (|Root|_|) = rootActivePattern
            match ParseArgs.Init loc.hash with
            | Str "#" (Root(v, End)) -> v
            | _ -> []

        let (|Page|_|) = function
            | Str "chargen/adnd/DarkSun" ctx ->
                let model'= Chargen.View.init()
                let cmd = [
                    Open (Page.Generate model', None)
                    SetMethod DarkSunMethodI |> ChargenMsg
                    ]
                Some(cmd, ctx)
            | Str "chargen/adnd" ctx ->
                let model' = Chargen.View.init()
                let cmd = [
                    Open (Page.Generate model', None)
                    ]
                Some(cmd, ctx)
            | Str "chargen/5e" ctx ->
                let model' = Chargen.View.init()
                let cmd = [
                    Open (Page.Generate model', None)
                    ChargenMsg (SetRuleset Chargen.View.WotC)
                    ]
                Some(cmd, ctx)
            | Str "chargen" ctx ->
                let model' = Chargen.View.init()
                let cmd = [
                    Open (Page.Generate model', None)
                    ]
                Some(cmd, ctx)
            | Str "resume/" (Int (id, ctx)) ->
                Some([ResumePlay id], ctx)
            | Str "/" ctx | Str "" (End as ctx) ->
                Some([GoHome], ctx)
            | _ -> None

        let page = locationParser (|Page|_|)
    let parse loc =
        let parsed = Parse.page loc
        parsed |> List.map Cmd.ofMsg
    let unpack (cmds: Cmd<Msg> list) model =
        model, Cmd.batch cmds

open App
open Elmish
open Elmish.Navigation

Program.mkProgram init update view
//|> Program.withSubscription(fun m -> Cmd.ofSub(fun dispatch ->
//    Browser.Dom.window.onerror <-
//    fun msg ->
//        if msg.ToString().Contains "SocketProtocolError" = false then
//            dispatch (sprintf "Error: %A" msg |> Error)
//            Browser.Dom.window.alert ("Unhandled Exception: " + msg.ToString())
//        ))
|> Program.toNavigable Url.parse Url.unpack
|> Program.withReactBatched "feliz-app"
|> Program.run
