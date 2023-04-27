// Proof of concept for Dungeon Fantasy difficulty computations

type RollTarget = int
type StatSummary = {
    bestAttack: RollTarget
    averageDamage: float
    numberOfAttacks: int
    bestDefense: RollTarget
    hp: int
}

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
            printfn $"{n}: {count} {float count / float total}"
            n, float count / float total
        ]
    |> Map.ofList
