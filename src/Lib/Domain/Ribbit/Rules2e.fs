module Domain.Ribbit.Rules2e
open Domain.Random
open Domain.Ribbit
open Domain.Treasure
open Domain.Character
open Domain.Character.ADND2nd

let traitsP = FlagsProperty<Trait>("Traits")
let getM = Ribbit.GetM

type MonsterKind = {
    name: string
    numberAppearing: RollSpec
    hd: RollSpec
    ac: int
    attacks: int
    toHit: int
    weaponDamage: RollSpec list
    traits: Trait list
    xp: int
    treasureType: TreasureType list
    lairTreasure: TreasureType list
    }
    with
    static member create (name, numberAppearing, hd, ac, attacks, toHit, weaponDamage, traits, xp, treasureType, lairTreasure) =
        {
        name = name; numberAppearing = numberAppearing; hd = hd; ac = ac;
        attacks = attacks; toHit = toHit; weaponDamage = weaponDamage; traits = traits
        xp = xp; treasureType = treasureType; lairTreasure = lairTreasure
        }

let load (monsterKind:MonsterKind) : StateChange<Ribbit, unit> =
    let initialize kindId = stateChange {
        do! transform (hdP.Set (kindId, monsterKind.hd))
        do! transform (acP.Set (kindId, monsterKind.ac))
        do! transform (toHitP.Set (kindId, monsterKind.toHit))
        do! transform (numberOfAttacksP.Set (kindId, monsterKind.attacks))
        do! transform (weaponDamageP.Set (kindId, monsterKind.weaponDamage))
        do! traitsP.SetAllM (kindId, monsterKind.traits |> List.map string |> Set.ofList)
        }
    stateChange {
        do! addKind monsterKind.name initialize
        }

let create (monsterKind: MonsterKind) (n: int) : StateChange<Ribbit, unit> =
    let initialize monsterId : StateChange<Ribbit, unit> = stateChange {
        // every monster has individual HD
        let! hdRoll = getF (hdP.Get monsterId)
        // always have at least 1 HP        
        do! (hpP.SetM (monsterId, hdRoll.roll() |> max 1))
        }
    stateChange {
        let! alreadyLoaded = Ribbit.GetM(fun ribbit -> ribbit.data.kindsOfMonsters.ContainsKey monsterKind.name)
        if alreadyLoaded |> not then
            do! load monsterKind
        for ix in 1..n do
            let! personalName = addMonster monsterKind.name initialize
            ()
        }

let monsterKinds =
    [
    let roll n d = RollSpec.create(n,d)
    let rollb n d (b: int) = RollSpec.create(n,d,b)
    "Jackal", roll 1 6, roll 1 4, 7, 1, +0, [roll 1 2], [], 7, [], []
    "Porcupine", roll 1 2, roll 1 4, 6, 1, +0, [roll 1 3], [], 15, [], []
    "Wolf", roll 2 6, roll 3 8, 7, 1, +2, [rollb 1 4 +1], [], 120, [], []
    "Kobold", roll 5 4, rollb 1 8 -1, 7, 1, +0, [roll 1 6], [], 7, [J], [O;Q;Q;Q;Q;Q]
    "Goblin", roll 4 6, rollb 1 8 -1, 6, 1, +0, [roll 1 6], [], 15, [K], [C]
    "Guard", roll 2 10, rollb 1 8 +1, 5, 1, +1, [roll 2 4], [], 35, [J;M;D], [Q;Q;Q;Q;Q]
    "Hobgoblin", roll 2 10, rollb 1 8 +1, 5, 1, +1, [roll 2 4], [], 35, [J;M], [D;Q;Q;Q;Q;Q]
    "Black Bear", roll 1 3, rollb 3 8 +3, 7, 2, +3, [roll 2 3; roll 1 6], [], 175, [], []
    "Owlbear", StaticBonus 1, rollb 5 8 +2, 5, 3, +5, [roll 1 6; roll 1 6; roll 2 6], [], 420, [], [C]
    "Hill Giant", roll 1 12, (roll 12 8 + roll 1 2), 3, 1, +11, [rollb 2 6 +7], [], 3000, [D], []
    "Frost Giant", roll 1 8, (roll 15 4 + roll 1 4), 0, 1, +15, [rollb 2 8 +9], [], 7000, [E], []
    ]
    |> List.map (fun args -> MonsterKind.create args |> fun monster -> monster.name, monster)
    |> Map.ofList

let createByName name n : StateChange<Ribbit, unit> =
    create (monsterKinds[name]) n

let attack ids id : StateChange<Ribbit, _> = stateChange {
    let! numberOfAttacks = numberOfAttacksP.Get id |> getM
    let! toHit = toHitP.Get id |> getM
    let! dmgs = weaponDamageP.Get id |> getM
    let! name = personalNameP.Get id |> getM
    let mutable msgs = []
    for ix in 1..numberOfAttacks do
        let! isAlive = getM(fun state -> hpP.Get id state > damageTakenP.Get id state)
        if isAlive then
            let! target = findTarget ids id // there seems to be a potential Fable bug with match! which can result in a dead target still getting hit (for another "fatal" blow)
            match target with               // so I'm using regular let! and match instead.
            | Some targetId ->
                let! targetName = personalNameP.Get targetId |> getM
                let! ac = acP.Get targetId |> getM
                match rand 20 with
                | 20 as n
                | n when n + toHit + ac >= 20 ->
                    let! targetDmg = damageTakenP.Get targetId |> getM
                    let dmg = dmgs[ix % dmgs.Length]
                    let damage = dmg.roll() |> max 0
                    do! damageTakenP.SetM(targetId, targetDmg + damage)
                    let! targetHp = hpP.Get targetId |> getM
                    let isFatal = targetDmg + damage >= targetHp
                    let killMsg = if isFatal then ", a fatal blow" else ""
                    let! isFriendly = isFriendlyP.Get id |> getM
                    msgs <- msgs@[LogEntry.create($"{name} hits {targetName} for {damage} points of damage{killMsg}! [Attack roll: {n}, Damage: {dmg} = {damage}]", isFatal, if isFriendly then Good else Bad)]
                | n ->
                    msgs <- msgs@[LogEntry.create $"{name} misses {targetName}. [Attack roll: {n}]"]
            | None -> ()
    return msgs
    }

let fightLogic = stateChange {
    let! ids = getM(fun state -> state.data.roster |> Map.values |> Array.ofSeq)
    let initiativeOrder =
        ids |> Array.map (fun id -> id, rand 10) |> Array.sortBy snd
    let mutable msgs = []
    for id, init in initiativeOrder do
        let! msgs' = attack ids id
        msgs <- msgs@msgs'

    let! factions = getM(fun state -> ids |> Array.filter(fun id -> hpP.Get id state > damageTakenP.Get id state) |> Array.groupBy (fun id -> isFriendlyP.Get id state) |> Array.map fst |> Array.sortDescending)
    let outcome = match factions with [|true|] -> Victory | [|true;false|] -> Ongoing | _ -> Defeat // mutual destruction is still defeat
    return outcome, msgs
    }

let fightOneRound (ribbit: Ribbit) : RoundResult =
    let (outcome, msg), ribbit = fightLogic ribbit
    { outcome = outcome ; msgs = msg; ribbit = ribbit }
