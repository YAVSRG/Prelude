﻿namespace Prelude.Charts.Tools.Patterns

open System.Collections.Generic
open Percyqaz.Json
open Prelude
open Prelude.Charts

[<RequireQualifiedAccess>]
type Direction =
    | None
    | Left
    | Right
    | Outwards
    | Inwards

type [<Measure>] rate
type ScaledTime = float32<ms/rate>

type RowInfo =
    {
        RawNotes: int array
        Notes: int
        Jacks: int
        Direction: Direction
        Roll: bool
        Time: float32<ms/rate>
        MsPerBeat: float32<ms/beat>
        Density: float32
    }

module Analysis =

    let private DENSITY_SENTITIVITY = -4f

    let private density_step(time: Time) (d: float32) =
        let seconds = time / 1000f<ms>
        d * System.MathF.Exp(DENSITY_SENTITIVITY * seconds)

    let private density_hit(time: Time) (d: float32) =
        let seconds = time / 1000f<ms>
        density_step time d + (1.0f - System.MathF.Exp(DENSITY_SENTITIVITY * seconds)) / seconds

    let density_data (rate: float32) (chart: Chart) =
        let column_densities = Array.zeroCreate chart.Keys
        let column_sinces = Array.create chart.Keys -Time.infinity
        seq {
            for { Time = t; Data = row } in chart.Notes do
                for k = 0 to chart.Keys - 1 do
                    if row.[k] = NoteType.NORMAL || row.[k] = NoteType.HOLDHEAD then 
                        column_densities.[k] <- density_hit ((t - column_sinces.[k]) / rate) column_densities.[k]
                        column_sinces.[k] <- t
                yield Array.max column_densities
        }
        |> Array.ofSeq

    let inline nps_cps (samples: int) (chart: ^T when ^T : (member FirstNote: Time) and ^T : (member LastNote: Time) and ^T : (member Notes: TimeArray<NoteRow>)) : int array * int array =
        let mutable i = 0
        let mutable notes = 0
        let mutable rows = 0
        let mutable last_sample = 0
        let mutable last_sample_rows = 0
        let start = chart.FirstNote
        let length = chart.LastNote - start
        let interval = length / float32 samples

        let notecounts = Array.zeroCreate samples
        let rowcounts = Array.zeroCreate samples

        if length > 0.0f<ms> then 

            for { Time = t; Data = row } in chart.Notes do
                let mutable is_empty = true
                for nt in row do
                    if nt = NoteType.NORMAL || nt = NoteType.HOLDHEAD then 
                        is_empty <- false
                        notes <- notes + 1
                if not is_empty then rows <- rows + 1
                while t - start >= interval * float32 (i + 1) do
                    notecounts.[i] <- notes - last_sample
                    rowcounts.[i] <- rows - last_sample_rows
                    last_sample <- notes
                    last_sample_rows <- rows
                    i <- i + 1
            if i <> samples then 
                notecounts.[samples - 1] <- notes - last_sample
                rowcounts.[samples - 1] <- rows - last_sample_rows

        notecounts, rowcounts

    let run (rate: float32) (chart: Chart) : RowInfo list =
    
        let { Time = first_note; Data = row } = (TimeArray.first chart.Notes).Value
        let density = density_data rate chart
        let mutable previous_row =
            seq {0 .. chart.Keys - 1}
            |> Seq.filter (fun x -> row.[x] = NoteType.NORMAL || row.[x] = NoteType.HOLDHEAD)
            |> Array.ofSeq
        let mutable previous_time = first_note

        let mutable index = 0

        seq {
            for { Time = t; Data = row } in (chart.Notes |> Seq.skip 1) do
                index <- index + 1
                let current_row = 
                    seq {0 .. chart.Keys - 1}
                    |> Seq.filter (fun x -> row.[x] = NoteType.NORMAL || row.[x] = NoteType.HOLDHEAD)
                    |> Array.ofSeq

                if current_row.Length > 0 then

                    let pmin = Array.min previous_row
                    let pmax = Array.max previous_row
                    let cmin = Array.min current_row
                    let cmax = Array.max current_row

                    yield {
                        RawNotes = current_row
                        Notes = current_row.Length
                        Jacks = current_row.Length - (Array.except previous_row current_row).Length
                        Direction =
                            let lo = cmin - pmin
                            let hi = cmax - pmax

                            if lo > 0 then
                                if hi > 0 then Direction.Right
                                else Direction.Inwards
                            elif lo < 0 then
                                if hi < 0 then Direction.Left
                                else Direction.Outwards
                            else
                                if hi < 0 then Direction.Inwards
                                elif hi > 0 then Direction.Outwards
                                else Direction.None
                        Roll = pmin > cmax || pmax < cmin
                        Time = (t - first_note) / (rate * 1.0f<rate>)
                        MsPerBeat = (t - previous_time) * 4.0f</beat> / rate
                        Density = density.[index]
                    }

                    previous_row <- current_row
                    previous_time <- t

        } |> List.ofSeq

[<Json.AutoCodec>]
type PatternId = 
    | Stream of string
    | Jack of string
    override this.ToString() = match this with Stream s | Jack s -> s
type Pattern = RowInfo list -> int

module Patterns =

    module Common =
        
        let STREAMS : Pattern = 
            function
            |      { Notes = 1; Jacks = 0; RawNotes = x }
                :: { Notes = 1; Jacks = 0 }
                :: { Notes = 1; Jacks = 0 }
                :: { Notes = 1; Jacks = 0 }
                :: { Notes = 1; Jacks = 0; RawNotes = y }
                :: _ when x.[0] <> y.[0] -> 5
            | _ -> 0

        let ALTERNATION : Pattern =
            function 
            |      { Jacks = 0; Direction = Direction.Right }
                :: { Jacks = 0; Direction = Direction.Left }
                :: _ -> 2
            |      { Jacks = 0; Direction = Direction.Left }
                :: { Jacks = 0; Direction = Direction.Right }
                :: _ -> 2
            | _ -> 0

        let JACKS : Pattern = 
            function
            |      { Notes = n }
                :: { Jacks = x }
                :: { Jacks = y }
                :: _ when x > 1 && y > 1 && x <= n && y <= x -> 3
            | _ -> 0

        let CHORDJACKS : Pattern = 
            function
            |   { Notes = a }
                :: { Notes = b; Jacks = j }
                :: _ when a > 2 && b > 1 && j >= 1 && (b < a || j < b) -> 2
            | _ -> 0

        let GLUTS : Pattern =
            function
            |   { Notes = a }
                :: { Notes = b; Jacks = 1 } 
                :: _ when a > 1 && b > 1 -> 2
            | _ -> 0

    module ``4K`` =

        let HANDSTREAM : Pattern =
            function
            |      { Notes = 3; Jacks = 0 }
                :: { Jacks = 0 }
                :: { Jacks = 0 }
                :: { Jacks = 0 }
                :: _ -> 4
            | _ -> 0

        let JUMPSTREAM : Pattern =
            function
            |      { Notes = 2; Jacks = 0 }
                :: { Jacks = 0 }
                :: { Notes = a; Jacks = 0 }
                :: { Notes = b; Jacks = 0 }
                :: _ when a < 3 && b < 3 -> 4
            | _ -> 0
            
        let DENSE_JUMPSTREAM : Pattern =
            function
            |      { Notes = 2; Jacks = 0 }
                :: { Notes = 1; Jacks = 0 }
                :: { Notes = 2; Jacks = 0 }
                :: { Notes = 1; Jacks = 0 }
                :: _ -> 4
            | _ -> 0

        let DOUBLE_JUMPSTREAM : Pattern =
            function
            |      { Notes = 1; Jacks = 0 }
                :: { Notes = 2; Jacks = 0 }
                :: { Notes = 2; Jacks = 0 }
                :: { Notes = 1; Jacks = 0 }
                :: _ -> 4
            | _ -> 0

        let TRIPLE_JUMPSTREAM : Pattern =
            function
            |      { Notes = 1; Jacks = 0 }
                :: { Notes = 2; Jacks = 0 }
                :: { Notes = 2; Jacks = 0 }
                :: { Notes = 2; Jacks = 0 }
                :: { Notes = 1; Jacks = 0 }
                :: _ -> 4
            | _ -> 0
        
        let JUMPTRILL : Pattern =
            function
            |      { Notes = 2 }
                :: { Notes = 2; Roll = true }
                :: { Notes = 2; Roll = true }
                :: { Notes = 2; Roll = true }
                :: _ -> 4
            | _ -> 0

        let SPLITTRILL : Pattern =
            function
            |      { Notes = 2 }
                :: { Notes = 2; Jacks = 0; Roll = false }
                :: { Notes = 2; Jacks = 0; Roll = false }
                :: _ -> 3
            | _ -> 0

        let ROLL : Pattern =
            function
            |      { Notes = 1; Direction = Direction.Left }
                :: { Notes = 1; Direction = Direction.Left }
                :: { Notes = 1; Direction = Direction.Left }
                :: _ -> 3
            |      { Notes = 1; Direction = Direction.Right }
                :: { Notes = 1; Direction = Direction.Right }
                :: { Notes = 1; Direction = Direction.Right }
                :: _ -> 3
            | _ -> 0

        let JUMPJACKS : Pattern =
            function
            |      { Notes = 2 }
                :: { Notes = 2; Jacks = 2 }
                :: _ -> 2
            | _ -> 0

        let JUMPGLUTS : Pattern =
            function
            |      { Notes = 2 }
                :: { Notes = 2; Jacks = 1 }
                :: _ -> 2
            | _ -> 0

    module ``7K`` = 

        let DOUBLE_STAIRS : Pattern =
            function
            |      { Notes = 2 }
                :: { Notes = 2; Jacks = 0; Direction = Direction.Left; Roll = false }
                :: _ -> 2
            |      { Notes = 2 }
                :: { Notes = 2; Jacks = 0; Direction = Direction.Right; Roll = false }
                :: _ -> 2
            | _ -> 0
            
        let DOUBLE_STREAMS : Pattern =
            function
            |      { Notes = 2 }
                :: { Notes = 2; Jacks = 0; Roll = false }
                :: _ -> 2
            | _ -> 0

        let DENSE_CHORDSTREAM : Pattern =
            function
            |      { Notes = x }
                :: { Notes = y; Jacks = 0 }
                :: _ when x > 1 && y > 1 -> 2
            | _ -> 0
            
        let LIGHT_CHORDSTREAM : Pattern =
            function
            |      { Notes = x }
                :: { Notes = y; Jacks = 0 }
                :: _ when x > 1 && y = 1 -> 2
            | _ -> 0

        let CHORD_ROLL : Pattern =
            function
            |      { Notes = x }
                :: { Notes = y; Direction = Direction.Left; Roll = true }
                :: { Notes = z; Direction = Direction.Left; Roll = true }
                :: _ when x > 1 && y > 1 && z > 1 -> 3
            |      { Notes = x }
                :: { Notes = y; Direction = Direction.Right; Roll = true }
                :: { Notes = z; Direction = Direction.Right; Roll = true }
                :: _ when x > 1 && y > 1 && z > 1 -> 3
            | _ -> 0

    type PatternToken = { Time: ScaledTime; MsPerBeat: float32<ms/beat>; Density: float32 }

    let matches (patterns: IDictionary<PatternId, Pattern>) (data: RowInfo list) : (PatternId * PatternToken) seq =
        let mutable data = data
        seq {
            while not data.IsEmpty do
                for pattern_name in patterns.Keys do
                    match patterns.[pattern_name] data with
                    | 0 -> ()
                    | 1 -> yield (pattern_name, { Time = data.Head.Time; MsPerBeat = data.Head.MsPerBeat; Density = data.Head.Density })
                    | n ->
                        let d = List.take n data
                        let mean_mspb = List.take n data |> List.averageBy (fun d -> d.MsPerBeat)

                        if d |> List.forall (fun d -> abs(d.MsPerBeat - mean_mspb) < 1.0f<ms/beat>) then
                            yield (pattern_name, { Time = data.Head.Time; MsPerBeat = mean_mspb; Density = d |> List.averageBy (fun d -> d.Density) })
                data <- List.tail data
        }

    let analysis_4k = dict [
            Stream "Streams", Common.STREAMS
            Stream "Jumpstream", ``4K``.JUMPSTREAM
            Stream "Jumptrill", ``4K``.JUMPTRILL
            Stream "Split trill", ``4K``.SPLITTRILL
            Stream "Rolls", ``4K``.ROLL
            Stream "Handstream", ``4K``.HANDSTREAM
            Jack "Jacks", Common.JACKS
            Jack "Chordjacks", Common.CHORDJACKS
            Jack "Jumpgluts", ``4K``.JUMPGLUTS
        ]

    let analysis_generic = dict [
            Stream "Streams", Common.STREAMS
            Stream "Light chordstream", ``7K``.LIGHT_CHORDSTREAM
            Stream "Dense chordstream", ``7K``.DENSE_CHORDSTREAM
            Jack "Jacks", Common.JACKS
            Jack "Chordjacks", Common.CHORDJACKS
            Jack "Gluts", Common.GLUTS
        ]

    let analysis_7k = dict [
            Stream "Streams", Common.STREAMS
            Stream "Light chordstream", ``7K``.LIGHT_CHORDSTREAM
            Stream "Dense chordstream", ``7K``.DENSE_CHORDSTREAM
            Stream "Double streams", ``7K``.DOUBLE_STREAMS
            Stream "Double stairs", ``7K``.DOUBLE_STAIRS
            Stream "Chord rolls", ``7K``.CHORD_ROLL
            Jack "Jacks", Common.JACKS
            Jack "Chordjacks", Common.CHORDJACKS
            Jack "Gluts", Common.GLUTS
        ]

    let analyse (rate: float32) (chart: Chart) =
        let data = Analysis.run rate chart
        if chart.Keys = 4 then matches analysis_4k data
        elif chart.Keys = 7 then matches analysis_7k data
        else matches analysis_generic data

    type PatternLocation = { Time: ScaledTime; Duration: ScaledTime; BPM: int; AverageDensity: float32 }

    type private BPMCluster = { mutable MsPerBeat: float32<ms/beat>; mutable Size: int }

    let private BPM_CLUSTER_THRESHOLD = 5.0f<ms/beat>

    let pattern_locations (pattern_tokens: (PatternId * PatternToken) seq) : (PatternId * PatternLocation) seq =
        let PATTERN_DURATION = 600.0f<ms/rate>

        let groups =
            pattern_tokens
            |> Seq.filter (fun (_, token) -> token.MsPerBeat <= (60000.0f<ms/minute> / 85.0f<beat/minute>))
            |> Seq.groupBy fst
            |> Array.ofSeq
            |> Array.map (fun (pattern, data) -> 
                    pattern, 
                    Seq.map snd data |> Array.ofSeq
                )

        let clusters = ResizeArray<BPMCluster>()
        let cluster value =
            match clusters |> Seq.tryFind (fun c -> abs (c.MsPerBeat - value) < BPM_CLUSTER_THRESHOLD) with
            | Some c -> 
                c.MsPerBeat <- (c.MsPerBeat * float32 c.Size + value) / (float32 c.Size + 1.0f)
                c.Size <- c.Size + 1
            | None -> clusters.Add({ Size = 1; MsPerBeat = value })
        for _, data in groups do
            for token in data do cluster token.MsPerBeat

        let patterns = ResizeArray<PatternId * PatternLocation>()

        for pattern_id, data in groups do
            let mutable current_n = 0
            let mutable current_mspb = 0.0f<ms/beat>
            let mutable current_density = 0.0f
            let mutable current_start = 0.0f<ms/rate>
            let mutable current_end = 0.0f<ms/rate>

            let finish() =
                let mspb = current_mspb / float32 current_n
                let density = current_density / float32 current_n
                let clustered_mspb =
                    match clusters |> Seq.tryFind (fun c -> abs (c.MsPerBeat - mspb) < BPM_CLUSTER_THRESHOLD) with
                    | Some c -> c.MsPerBeat
                    | None -> mspb
                let bpm = (60000.0f<ms/minute> / clustered_mspb |> float32 |> round |> int)

                patterns.Add((pattern_id, { BPM = bpm; Time = current_start; Duration = current_end - current_start; AverageDensity = density }))
                current_n <- 0

            for token in data do
                if current_n > 0 && (abs ((current_mspb / float32 current_n) - token.MsPerBeat) < BPM_CLUSTER_THRESHOLD) && token.Time <= current_end then
                    current_n <- current_n + 1
                    current_mspb <- current_mspb + token.MsPerBeat
                    current_density <- current_density + token.Density
                    current_end <- token.Time + PATTERN_DURATION
                else
                    if current_n > 0 then current_end <- min current_end token.Time; finish()
                    current_n <- 1
                    current_mspb <- token.MsPerBeat
                    current_density <- token.Density
                    current_start <- token.Time
                    current_end <- token.Time + PATTERN_DURATION

            finish()

        patterns

    type PatternBreakdown = 
        { 
            mutable TotalTime: ScaledTime
            mutable DensityTime: ScaledTime
            mutable Bursts: ResizeArray<ScaledTime * ScaledTime>
            mutable Runs: ResizeArray<ScaledTime * ScaledTime>
            mutable Sprints: ResizeArray<ScaledTime * ScaledTime>
            mutable Marathons: ResizeArray<ScaledTime * ScaledTime>
        }

    let pattern_breakdown (patterns: (PatternId * PatternLocation) seq) =
        
        let coverage = Dictionary<PatternId * int, PatternBreakdown>()
        for (pattern, info) in patterns do
            let key = (pattern, info.BPM)
            if not <| coverage.ContainsKey(key) then coverage.Add(key, { TotalTime = 0.0f<ms/rate>; DensityTime = 0.0f<ms/rate>; Bursts = ResizeArray<_>(); Runs = ResizeArray<_>(); Sprints = ResizeArray<_>(); Marathons = ResizeArray<_>() })
            coverage.[key].TotalTime <- coverage.[key].TotalTime + info.Duration
            coverage.[key].DensityTime <- coverage.[key].DensityTime + info.Duration * info.AverageDensity
            match info.Duration with
            | x when x < 2000.0f<ms/rate> -> coverage.[key].Bursts.Add((info.Time, info.Duration))
            | x when x < 5000.0f<ms/rate> -> coverage.[key].Runs.Add((info.Time, info.Duration))
            | x when x < 30000.0f<ms/rate> -> coverage.[key].Sprints.Add((info.Time, info.Duration))
            | _ -> coverage.[key].Marathons.Add((info.Time, info.Duration))

        coverage

    let ln_percent (chart: Chart) : float32 =
        let mutable notes = 0
        let mutable lnotes = 0

        for { Data = nr } in chart.Notes do
            for n in nr do
                if n = NoteType.NORMAL then
                    notes <- notes + 1
                elif n = NoteType.HOLDHEAD then
                    notes <- notes + 1
                    lnotes <- lnotes + 1

        float32 lnotes / float32 notes

    let sv_time (chart: Chart) : Time =
        if chart.SV.Length = 0 then 0.0f<ms> else

        let mutable total = 0.0f<ms>

        let mutable time = chart.FirstNote
        let mutable vel = 1.0f
        for sv in chart.SV do
            if not (System.Single.IsFinite vel) || abs(vel - 1.0f) > 0.01f then
                total <- total + (sv.Time - time)
            vel <- sv.Data
            time <- sv.Time
            
        if not (System.Single.IsFinite vel) || abs(vel - 1.0f) > 0.01f then
            total <- total + (chart.LastNote - time)

        total

    [<Json.AutoCodec>]
    type PatternReportEntry = { Pattern: PatternId; BPM: int; Score: float32<ms/rate> }
    [<Json.AutoCodec>]
    type PatternReport = { Patterns: PatternReportEntry list; LNPercent: float32; SVAmount: Time }
    let generate_pattern_report (rate: float32, chart: Chart) : PatternReport =
        let data = 
            analyse rate chart
            |> pattern_locations
            |> pattern_breakdown

        let importance (p, bpm) =
            match p with
            | Stream s -> float32 (bpm * bpm) * 0.25f
            | Jack s -> float32 (bpm * bpm)

        {
            Patterns = 
                data.Keys 
                |> Seq.map ( fun (p, bpm) -> { Pattern = p; BPM = bpm; Score = data.[(p, bpm)].TotalTime * importance (p, bpm) / 1_000_000.0f } )
                |> Seq.sortByDescending (fun x -> x.Score )
                |> List.ofSeq
                |> List.truncate 10
            LNPercent = ln_percent chart
            SVAmount = sv_time chart
        }

    let categorise_chart (report: PatternReport) =
        let isJack = fun e -> match e.Pattern with Jack _ -> true | _ -> false
        let isStream = fun e -> match e.Pattern with Stream _ -> true | _ -> false
        let jacks = report.Patterns |> List.filter isJack
        let streams = report.Patterns |> List.filter isStream

        let total = report.Patterns |> List.sumBy (fun e -> e.Score)
        let stream_total = streams |> List.sumBy (fun e -> e.Score)
        let jack_total = jacks |> List.sumBy (fun e -> e.Score)

        if stream_total / total > 0.3f && jack_total / total > 0.3f then
            "Hybrid"
        elif stream_total / total > 0.4f then
            (List.head streams).Pattern.ToString()
        elif jack_total / total > 0.4f then 
            (List.head jacks).Pattern.ToString()
        else "Unknown"

