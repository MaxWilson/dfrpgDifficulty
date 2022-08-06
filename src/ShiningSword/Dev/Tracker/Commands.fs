module Dev.App.Tracker.Commands

open Dev.App.Tracker.Game
open Packrat

let helpText = """
Example commands:
define Beholder
add Beholder
Beholder hp 180, xp 10000
add Bob, Lara, Harry
Harry hits Beholder #1 for 80
Beholder #1 hits Lara for 30
Beholder #1 hits Bob for 60
clear dead
Lara declares Kill beholder
roll init
next init
"""

#nowarn "40" // we're not going anything crazy with recursion like calling a pass-in method as part of a ctor. Just regular pattenr-matching.

let nameChars = alphanumeric + whitespace + Set ['#']
let (|NewName|_|) = function
    | OWS(Chars nameChars (name, ctx)) -> Some(DataTypes.Name (name.Trim()), ctx)
    | _ -> None
let rec (|NewNames|_|) = pack <| function
    | NewName(name, OWSStr "," (NewNames(rest, ctx))) -> Some(name::rest, ctx)
    | NewName(name, ctx) -> Some([name], ctx)
    | _ -> None
let (|GameContext|_|) = ExternalContextOf<Game.d>
let isPotentialNamePrefix (names: obj) (substring: string) =
    match names |> unbox<obj option> with
    | Some externalContext ->
        let game = externalContext |> unbox<Game.d>
        game.roster |> Seq.append game.bestiary.Keys |> Seq.exists(fun (DataTypes.Name name) -> name.StartsWith substring)
    | _ -> false
let (|Name|_|) = function
    | OWS(GameContext(game) & (args, ix)) ->
        let substring = args.input.Substring(ix)
        let candidates = game.bestiary.Keys |> Seq.append game.roster |> Seq.distinct |> Seq.sortByDescending (fun (DataTypes.Name n) -> n.Length)
        // allow leaving off # sign
        match candidates |> Seq.tryFind(fun (DataTypes.Name name) -> substring.StartsWith name || substring.StartsWith (name.Replace("#", ""))) with
        | Some (DataTypes.Name n as name) ->
            let l = if substring.StartsWith (n.Replace("#", "")) then (n.Replace("#", "").Length) else n.Length
            Some(name, (args, ix+l))
        | None -> None
    | _ -> None
let rec (|Names|_|) = pack <| function
| Name(name, Str "," (Names(rest, ctx))) -> Some(name::rest, ctx)
| Name(name, ctx) -> Some([name], ctx)
| _ -> None
let (|Declaration|_|) = function
    | OWSStr "xp" (Int (amt, ctx)) ->
        Some((fun name -> Game.DeclareXP(name, XP amt)), ctx)
    | OWSStr "hp" (Int (amt, ctx)) ->
        Some((fun name -> Game.DeclareHP(name, HP amt)), ctx)
    | _ -> None
let rec (|Declarations|_|) = pack <| function
    | Declaration(f, OWSStr "," (Declarations(rest, ctx))) -> Some(f::rest, ctx)
    | Declaration(f, ctx) -> Some([f], ctx)
    | _ -> None
let (|Command|_|) = function
    | Str "clear dead" ctx ->
        Some(Game.ClearDeadCreatures, ctx)
    | Str "add" (NewName(name, ctx)) ->
        Some(Game.Add(name), ctx)
    | Str "remove" (Names(names, ctx)) ->
        Some(Game.Remove names, ctx)
    | Str "rename" (Name(name, NewName(newName, ctx))) ->
        Some(Game.Rename (name, newName), ctx)
    | Str "define" (NewName(name, ctx)) ->
        Some(Game.Define(name), ctx)
    | Name(name, Declaration (f, ctx)) ->
        Some(f name, ctx)
    | Name(src, (OWSStr "hits" (Name(target, OWSStr "for" (Int(amt, ctx)))))) ->
        Some(Game.InflictDamage(src, target, HP amt), ctx)
    | _ -> None
let (|Commands|_|) = pack <| function
    | Str "add" (NewNames(names, ctx)) ->
        Some(names |> List.map Game.Add, ctx)
    | Str "define" (NewNames(names, ctx)) ->
        Some(names |> List.map Game.Define, ctx)
    | Name(name, Declarations (fs, ctx)) ->
        Some(fs |> List.map(fun f -> f name), ctx)
    | Command(cmd, ctx) -> Some([cmd], ctx)
    | _ -> None
let testbed() =
    let exec str game =
        match ParseArgs.Init(str, game) with
        | Commands(cmds, End) -> cmds |> List.fold (flip Game.update) game
    let mutable g = Game.fresh
    match ParseArgs.Init("Beholder hp 180, xp 10000", g) with
    | Commands(cmds, End) -> cmds
    iter &g (exec "define Beholder, Ogre")
    iter &g (exec "Beholder hp 180, xp 10000")
    iter &g (exec "define Giant")
    iter &g (exec "Giant hp 80")
    iter &g (exec "Giant xp 2900")
    iter &g (exec "add Giant")
    iter &g (exec "add Bob")
    iter &g (exec "Bob hp 50")
    iter &g (exec "Bob hits Giant 1 for 30")
    g.stats[DataTypes.Name "Giant #1"].HP
    iter &g (exec "Bob hits Giant 1 for 30")
    g.stats[DataTypes.Name "Giant #1"].HP
    iter &g (exec "Bob hits Giant 1 for 30")
    g.stats[DataTypes.Name "Giant #1"].HP
    iter &g (exec "clear dead")