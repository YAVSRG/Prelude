﻿namespace Prelude.Gameplay

open Percyqaz.Json

[<RequireQualifiedAccess>]
type Improvement<'T> =
    | FasterBetter of rate_increase: float32 * improvement: 'T
    | Faster of rate_increase: float32
    | Better of improvement: 'T
    | New
    | None

[<Json.AutoCodec>]
type PersonalBests<'T> = { Best: 'T * float32; Fastest: 'T * float32 }

module PersonalBests =

    let create (value: 'T, rate: float32) : PersonalBests<'T> = { Best = value, rate; Fastest = value, rate }

    let map (f: 'T -> 'U) ({ Best = best_overall, best_rate; Fastest = best_fastest, fastest_rate }: PersonalBests<'T>) : PersonalBests<'U> =
        { Best = f best_overall, best_rate; Fastest = f best_fastest, fastest_rate }

    let inline update (value: 'T, rate: float32) ({ Best = best_overall, best_rate; Fastest = best_fastest, fastest_rate }: PersonalBests<'T>) =

        let new_fastest_best, f_rate_increase, f_improvement =
            if rate > fastest_rate then (value, rate), Some (rate - fastest_rate), None
            elif rate = fastest_rate then
                if value > best_fastest then (value, rate), None, Some (value - best_fastest)
                else (best_fastest, rate), None, None
            else (best_fastest, fastest_rate), None, None

        let new_overall_best, b_rate_increase, b_improvement =
            if value > best_overall then (value, rate), None, Some (value - best_overall)
            elif value = best_overall then
                if rate > best_rate then (value, rate), Some (rate - best_rate), None
                else (best_overall, best_rate), None, None
            else (best_overall, best_rate), None, None

        let result = { Best = new_overall_best; Fastest = new_fastest_best }
        let info =

            let rate_increase =
                match f_rate_increase, b_rate_increase with
                | Some f, Some b -> Some (max f b)
                | Some f, None -> Some f
                | None, Some b -> Some b
                | None, None -> None
            
            let improvement =
                match f_improvement, b_improvement with
                | Some f, Some b -> Some (max f b)
                | Some f, None -> Some f
                | None, Some b -> Some b
                | None, None -> None

            match rate_increase, improvement with
            | Some r, Some i -> Improvement.FasterBetter (r, i)
            | Some r, None -> Improvement.Faster r
            | None, Some i -> Improvement.Better i
            | None, None -> Improvement.None

        result, info

    let best_this_rate (rate: float32) ({ Best = p1, r1; Fastest = p2, r2 }: PersonalBests<'T>) : 'T option =
        if r1 < rate then
            if r2 < rate then None else Some p2
        else Some p1

type PersonalBestsV2<'T> = ('T * float32) list

module PersonalBestsV2 =

    let rec get (minimum_rate: float32) (bests: PersonalBestsV2<'T>) =
        match bests with
        | [] -> None
        | (value, rate) :: xs -> 
            if rate = minimum_rate then Some value 
            elif rate < minimum_rate then None
            else get minimum_rate xs |> function None -> Some value | Some x -> Some x

    let inline update (value: 'T, rate: float32) (bests: PersonalBestsV2<'T>) : PersonalBestsV2<'T> * Improvement<'T> =
        let rec remove_worse_breakpoints (v: 'T) (bests: PersonalBestsV2<'T>) =
            match bests with
            | [] -> []
            | (value, _) :: xs when value <= v -> remove_worse_breakpoints v xs
            | xs -> xs
        let rec loop (xs: PersonalBestsV2<'T>) : PersonalBestsV2<'T> * Improvement<'T> =
            match xs with
            | [] -> (value, rate) :: [], Improvement.New
            | (v, r) :: xs ->
                if rate < r then
                    let res, imp = loop xs in (v, r) :: res, imp
                elif rate = r && value > v then
                    (value, rate) :: remove_worse_breakpoints value xs, Improvement.Better (value - v)
                elif rate = r then
                    (v, r) :: xs, Improvement.None
                else
                    if value > v then (value, rate) :: remove_worse_breakpoints value xs, Improvement.FasterBetter(rate - r, value - v)
                    elif value = v then (value, rate) :: remove_worse_breakpoints value xs, Improvement.Faster(rate - r)
                    else (value, rate) :: (v, r) :: xs, Improvement.New
        loop bests