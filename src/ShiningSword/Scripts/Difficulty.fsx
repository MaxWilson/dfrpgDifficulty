// Proof of concept for Dungeon Fantasy difficulty computations

type RollTarget = int
type StatSummary = {
    name: string
    bestAttack: RollTarget
    averageDamage: float
    numberOfAttacks: int
    bestDefense: RollTarget
    HP: int
    DR: int
}

// damage: 2d+1
let damagePointDistribution =
    let pointDistribution =
        [   for x in 1..6 do
                for y in 1..6 do
                    x+y+1
            ]
        |> List.groupBy id
        |> List.map (fun (n, lst) -> n, List.length lst)
    let total = 6*6
    [   for (n, nCount) in pointDistribution do
            n, float nCount / float total
        ]
    |> Map.ofList
let dpd = damagePointDistribution

let rollCumulativeDistribution =
    let pointDistribution =
        [   for x in 1..6 do
                for y in 1..6 do
                    for z in 1..6 do
                        x+y+z
            ]
        |> List.groupBy id
        |> List.map (fun (n, lst) -> n, List.length lst)
    let total = 6*6*6
    [   let mutable count = 0
        for (n, nCount) in pointDistribution do
            count <- count + nCount
            n, float count / float total
        ]
    |> Map.ofList
let rcd = rollCumulativeDistribution

let stats = {
    bestDefense = 12
    bestAttack = 16
    averageDamage = 6.
    HP = 10
    DR = 4
    numberOfAttacks = 1
    }
let durability (stats: StatSummary) =
    let failedDefense = 1. - rcd[stats.bestDefense]
    let armorFactor = dpd |> Seq.sumBy (function KeyValue(damage, probability) -> float (damage - stats.DR |> max 0) / 8. * probability)
    let defenseFactor = 1./failedDefense * armorFactor
    float stats.HP * defenseFactor
let offense (stats: StatSummary) =
    stats.averageDamage * rcd[stats.bestAttack]
let rating stats =
    offense stats * durability stats
let rate statsList =
    for stats in statsList do
        printfn $"{stats.name}: {rating stats} [offense {offense stats}] [defense {durability stats}]"
rate [
    {
        name = "Troll"
        bestDefense = 11
        bestAttack = 16
        averageDamage = 12.
        HP = 20
        DR = 0
        numberOfAttacks = 3
        }
    {
        name = "Troll"
        bestDefense = 11
        bestAttack = 16
        averageDamage = 12.
        HP = 20
        DR = 0
        numberOfAttacks = 3
        }
    {
        name = "Troll"
        bestDefense = 11
        bestAttack = 16
        averageDamage = 12.
        HP = 20
        DR = 0
        numberOfAttacks = 3
        }
    {
        name = "Peshkali"
        bestDefense = 11
        bestAttack = 16
        averageDamage = (13.5)*1.5
        HP = 20
        DR = 0
        numberOfAttacks = 3
        }
    {
        name = "Black Pudding"
        bestDefense = 9
        bestAttack = 14
        averageDamage = 18
        HP = 80
        DR = 5
        numberOfAttacks = 3
        }
    ]
