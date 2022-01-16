﻿namespace Prelude.Scoring

open System
open Prelude.Common
open Prelude.ChartFormats.Interlude

type JudgementId = int
/// Judgements are an indicator of how good a hit was, like "Perfect!" or "Nearly!"
/// Scores are commonly measured by how many of each judgement you get (for example a good score might be getting all "Perfect!" judgements)
type Judgement =
    {
        Name: string
        Color: Color
        BreaksCombo: bool
    }

/// Assignment of points per hit
/// Your total points are the sum of the points for each note you hit
/// Your % accuracy is number of points you get / max points possible
[<RequireQualifiedAccess>]
type AccuracyPoints =
    | WifeCurve of judge: int
    | Weights of maxweight: float * weights: float array

/// Behaviour for hold notes
[<RequireQualifiedAccess>]
type HoldNoteBehaviour =
    | Osu of od: float32
    | JustBreakCombo
    | Normal of {| JudgementIfDropped: JudgementId; JudgementIfOverheld: JudgementId |}
    | JudgeReleases of {| Timegates: (Time * JudgementId) list |}
    | OnlyJudgeReleases // uses base timegates

/// Grades are awarded at the end of a score as a summarising "rank" of how well you did
/// They typically follow lettering systems similar to academic exam grades
type Grade =
    {
        Name: string
        Accuracy: float
        Color: Color
    }

/// Lamps are awarded at the end of the score as a summarising "tag" to indicate certain accomplishments
/// Examples: You didn't miss a single note, so you get a "Full Combo" tag, you only got "Perfect" judgements, so you get a "Perfect Full Combo" tag
/// These provide alternative accomplishments to grades that can provide different challenge
type Lamp =
    {
        Name: string
        Judgement: JudgementId
        JudgementThreshold: int
        Color: Color
    }

type GradingConfig =
    {
        Grades: Grade array
        Lamps: Lamp array
    }

type HealthBarConfig =
    {
        StartingHealth: float
        OnlyFailAtEnd: bool
        ClearThreshold: float
        Points: float array
    }

type AccuracyConfig =
    {
        MissWindow: Time
        CbrushWindow: Time
        Timegates: (Time * JudgementId) list
        Points: AccuracyPoints
        HoldNoteBehaviour: HoldNoteBehaviour
    }

type ScoreSystemConfig =
    {
        Name: string
        Judgements: Judgement array
        Accuracy: AccuracyConfig
        Health: HealthBarConfig
        Grading: GradingConfig
    }
    member this.DefaultJudgement : JudgementId = this.Judgements.Length - 1
    member this.GradeName i = if i < 0 then "F" else this.Grading.Grades.[i].Name
    member this.GradeColor i = if i < 0 then Color.Gray else this.Grading.Grades.[i].Color
    member this.LampName i = if i < 0 then "NONE" else  this.Grading.Lamps.[i].Name
    member this.LampColor i = if i < 0 then Color.White else this.Grading.Lamps.[i].Color
    member this.JudgementName i = this.Judgements.[i].Name
    member this.JudgementColor i = this.Judgements.[i].Color
module ScoreSystemConfig =

    open System.IO
    open System.Security.Cryptography

    let hash (config: ScoreSystemConfig) =
        let h = SHA256.Create()
        use ms = new MemoryStream()
        use bw = new BinaryWriter(ms)

        for j in config.Judgements do
            bw.Write j.BreaksCombo
        bw.Write (float32 config.Accuracy.MissWindow)
        bw.Write (float32 config.Accuracy.CbrushWindow)
        for t, j in config.Accuracy.Timegates do
            bw.Write (float32 t)
            bw.Write j
        match config.Accuracy.Points with
        | AccuracyPoints.WifeCurve j -> bw.Write j
        | AccuracyPoints.Weights (max, pts) ->
            bw.Write max
            for p in pts do bw.Write p
        match config.Accuracy.HoldNoteBehaviour with
        | HoldNoteBehaviour.Osu od -> bw.Write od
        | HoldNoteBehaviour.JustBreakCombo -> bw.Write 0s
        | HoldNoteBehaviour.Normal rules -> bw.Write rules.JudgementIfDropped; bw.Write rules.JudgementIfOverheld
        | HoldNoteBehaviour.JudgeReleases d ->
            for t, j in d.Timegates do
                bw.Write (float32 t)
                bw.Write j
        | HoldNoteBehaviour.OnlyJudgeReleases -> bw.Write 1s
        bw.Write config.Health.StartingHealth
        bw.Write config.Health.ClearThreshold
        bw.Write config.Health.OnlyFailAtEnd
        for p in config.Health.Points do
            bw.Write p
        for g in config.Grading.Grades do
            bw.Write g.Accuracy
        for l in config.Grading.Lamps do
            bw.Write l.Judgement
            bw.Write l.JudgementThreshold

        let s =
            ms.ToArray()
            |> h.ComputeHash
            |> BitConverter.ToString
        config.Name.Replace(" ", "") + s.Replace("-", "").Substring(0, 6)

type HitEventGutsInternal =
    | Hit_ of
        delta: Time *
        isHoldHead: bool *
        missed: bool
    | Release_ of
        delta: Time *
        missed: bool *
        overhold: bool *
        dropped: bool

type HitEventGuts =
    | Hit of 
        {| 
            Judgement: JudgementId option
            Missed: bool
            Delta: Time
            /// True if this is the head of a hold note
            IsHold: bool
        |}
    | Release of
        {| 
            Judgement: JudgementId option
            Missed: bool
            Delta: Time
            /// True if the hold was pressed correctly and held too long, past the window to release it
            Overhold: bool 
            /// True if at any point (including missing the head) the hold was released when it shouldn't have been
            Dropped: bool
        |}

type HitEvent<'Guts> =
    {
        Time: ChartTime
        Column: int
        Guts: 'Guts
    }

(*
    Health bar system to be attached to other metrics (Bars are dependent on/coupled with the judgements scored according to other systems)
    These maintain a meter that fills as you hit notes well, and depletes as you hit notes badly (or miss)
    Falling below a certain level of "health" causes you to "fail" a chart
        Players can choose what the consequences of for failure (ending play immediately, just a flag on score screen, etc)

    In some games health bar is the main metric for competence at a chart
        Here its purpose is primarily to push players away from charts that are clearly too hard rather than being the arbiter
    (But things are complete enough here for you to simulate clear-oriented play as in those certain games e.g. BMS)
*)

type HealthBarState =
    {
        mutable HasFailed: bool
        mutable CurrentlyFailed: bool
        mutable Health: float
    }

type HealthBarMetric(config: HealthBarConfig) =

    member val State: HealthBarState = 
        { 
            HasFailed = false
            CurrentlyFailed = false
            Health = config.StartingHealth
        }

    member this.ChangeHP (x: float) =
        let newHP = Math.Clamp(this.State.Health + x, 0.0, 1.0)
        this.State.Health <- newHP
        if this.FailCondition newHP then
            this.State.HasFailed <- true
            this.State.CurrentlyFailed <- true
        else this.State.CurrentlyFailed <- false
        
    member this.Failed = if config.OnlyFailAtEnd then this.State.CurrentlyFailed else this.State.HasFailed
    member this.Format() = sprintf "%.2f%%" (this.State.Health * 100.0)

    member this.FailCondition hp = hp <= config.ClearThreshold

    member this.HandleEvent ev =
        match ev.Guts with
        | Hit evData -> match evData.Judgement with Some j -> this.ChangeHP(config.Points[j]) | None -> ()
        | Release evData -> match evData.Judgement with Some j -> this.ChangeHP(config.Points[j]) | None -> ()

(*
    Accuracy/scoring system metric.
    Each note you hit is assigned a certain number of points - Your % accuracy is points scored out of the possible maximum.
    Combo/combo breaking also built-in - Your combo is the number of notes hit well in a row
*)

type AccuracySystemState =
    {
        Judgements: int array
        mutable PointsScored: float
        mutable MaxPointsScored: float
        mutable CurrentCombo: int
        mutable BestCombo: int
        mutable MaxPossibleCombo: int
        mutable ComboBreaks: int
    }
    member this.BreakCombo (wouldHaveIncreasedCombo: bool) =
        if wouldHaveIncreasedCombo then this.MaxPossibleCombo <- this.MaxPossibleCombo + 1
        this.CurrentCombo <- 0
        this.ComboBreaks <- this.ComboBreaks + 1

    member this.IncrCombo() =
        this.MaxPossibleCombo <- this.MaxPossibleCombo + 1
        this.CurrentCombo <- this.CurrentCombo + 1
        this.BestCombo <- Math.Max(this.CurrentCombo, this.BestCombo)

    member this.Add(points: float, maxpoints: float, judge: JudgementId) =
        this.PointsScored <- this.PointsScored + points
        this.MaxPointsScored <- this.MaxPointsScored + maxpoints
        this.Judgements.[judge] <- this.Judgements.[judge] + 1

    member this.Add(judge: JudgementId) = this.Add(0.0, 0.0, judge)

type private HoldState =
    | Nothing
    | Holding
    | Dropped
    | MissedHead

[<AbstractClass>]
type IScoreMetric
    (
        config: ScoreSystemConfig,
        healthBar: HealthBarMetric,
        keys: int,
        replayProvider: IReplayProvider,
        notes: TimeData<NoteRow>,
        rate: float32
    ) =
    inherit ReplayConsumer(keys, replayProvider)

    let firstNote = offsetOf notes.First.Value
    let missWindow = config.Accuracy.MissWindow * rate

    // having two seekers improves performance when feeding scores rather than playing live
    let mutable noteSeekPassive = 0
    let mutable noteSeekActive = 0

    let internalHoldStates = Array.create keys (Nothing, -1)

    let hitData = InternalScore.createDefault config.Accuracy.MissWindow keys notes
    let hitEvents = ResizeArray<HitEvent<HitEventGuts>>()

    let mutable hitCallback = fun ev -> ()

    member val State =
        {
            Judgements = Array.zeroCreate config.Judgements.Length
            PointsScored = 0.0
            MaxPointsScored = 0.0
            CurrentCombo = 0
            BestCombo = 0
            ComboBreaks = 0
            MaxPossibleCombo = 0
        }

    member this.Name = config.Name
    member this.Value =
        let v = this.State.PointsScored / this.State.MaxPointsScored
        if Double.IsNaN v then 1.0 else v
    member this.FormatAccuracy() = sprintf "%.2f%%" (this.Value * 100.0)
    member this.HP = healthBar
    member this.MissWindow = config.Accuracy.MissWindow
    member this.ScaledMissWindow = missWindow

    member this.IsHoldDropped (index: int) (k: int) =
        match internalHoldStates.[k] with
        | Dropped, i | MissedHead, i when i >= index -> true
        | _ -> false

    member this.HitData = hitData

    member this.SetHitCallback (func: HitEvent<HitEventGuts> -> unit) = hitCallback <- func

    member this.Finished = noteSeekPassive = hitData.Length

    member this.HitEvents = hitEvents.AsReadOnly()

    // correctness guaranteed up to the time you update, no matter how you update
    // call Update with Time.infinity to do a correct feeding of the whole replay
    member this.Update (relativeTime: ChartTime) =
        this.PollReplay relativeTime // calls HandleKeyDown and HandleKeyUp appropriately
        this.HandlePassive relativeTime

    member private this.HandlePassive (relativeTime: ChartTime) =
        let now = firstNote + relativeTime
        let target = now - missWindow
        while noteSeekPassive < hitData.Length && InternalScore.offsetOf hitData.[noteSeekPassive] <= target do
            let struct (t, deltas, status) = hitData.[noteSeekPassive]
            for k = 0 to (keys - 1) do

                if status.[k] = HitStatus.HIT_REQUIRED then
                    this._HandleEvent { Time = t - firstNote + missWindow; Column = k; Guts = Hit_ (deltas.[k], false, true) }

                elif status.[k] = HitStatus.HIT_HOLD_REQUIRED then
                    internalHoldStates.[k] <- MissedHead, noteSeekPassive
                    this._HandleEvent { Time = t - firstNote + missWindow; Column = k; Guts = Hit_ (deltas.[k], true, true) }

                elif status.[k] = HitStatus.RELEASE_REQUIRED then
                    let overhold =
                        match internalHoldStates.[k] with
                        | Dropped, i | Holding, i when i <= noteSeekPassive -> true
                        | _ -> false
                        && Bitmap.hasBit k this.KeyState
                    let dropped =
                        match internalHoldStates.[k] with
                        | Dropped, _
                        | MissedHead, _ -> true
                        | _ -> false
                    this._HandleEvent { Time = t - firstNote + missWindow; Column = k; Guts = Release_ (deltas.[k], true, overhold, dropped) }
                    match internalHoldStates.[k] with
                    | _, i when i < noteSeekPassive -> internalHoldStates.[k] <- Nothing, noteSeekPassive
                    | _ -> ()

            noteSeekPassive <- noteSeekPassive + 1

    override this.HandleKeyDown (relativeTime: ChartTime, k: int) =
        this.HandlePassive relativeTime
        let now = firstNote + relativeTime
        while noteSeekActive < hitData.Length && InternalScore.offsetOf hitData.[noteSeekActive] < now - missWindow do 
            noteSeekActive <- noteSeekActive + 1

        let mutable i = noteSeekActive
        let mutable delta = missWindow
        let mutable found = -1
        let target = now + missWindow

        while i < hitData.Length && InternalScore.offsetOf hitData.[i] <= target do
            let struct (t, deltas, status) = hitData.[i]
            let d = now - t
            if status.[k] = HitStatus.HIT_REQUIRED || status.[k] = HitStatus.HIT_HOLD_REQUIRED then
                if (Time.Abs delta > Time.Abs d) then
                    found <- i
                    delta <- d
            // Accept a hit that looks like it's intended for a previous badly hit note that was fumbled early (preventing column lock)
            elif status.[k] = HitStatus.HIT_ACCEPTED && deltas.[k] < -config.Accuracy.CbrushWindow then
                if (Time.Abs delta > Time.Abs d) then
                    found <- i
                    delta <- d
            i <- i + 1

        if found >= 0 then
            let struct (t, deltas, status) = hitData.[found]
            if status.[k] <> HitStatus.HIT_ACCEPTED then // Could be an already hit note, in which case just swallow the extra input
                let isHoldHead = status.[k] <> HitStatus.HIT_REQUIRED
                status.[k] <- HitStatus.HIT_ACCEPTED
                deltas.[k] <- delta / rate
                this._HandleEvent { Time = relativeTime; Column = k; Guts = Hit_ (deltas.[k], isHoldHead, false) }
                // Begin tracking if it's a hold note
                //assert(fst internalHoldStates.[k] = Nothing)
                if isHoldHead then internalHoldStates.[k] <- Holding, found
        else // If no note to hit, but a hold note head was missed, pressing key marks it dropped instead
            internalHoldStates.[k] <- 
                match internalHoldStates.[k] with
                | MissedHead, i -> Dropped, i
                | x -> x
                
    override this.HandleKeyUp (relativeTime: ChartTime, k: int) =
        this.HandlePassive relativeTime
        let now = firstNote + relativeTime
        match internalHoldStates.[k] with
        | Holding, holdHeadIndex
        | Dropped, holdHeadIndex ->

            let mutable i = holdHeadIndex + 1
            let mutable delta = missWindow
            let mutable found = -1
            let target = now + missWindow

            while i < hitData.Length && InternalScore.offsetOf hitData.[i] <= target do
                let struct (t, _, status) = hitData.[i]
                let d = now - t
                if status.[k] = HitStatus.RELEASE_REQUIRED then
                    // Get the first unreleased hold tail we see, after the head of the hold we're tracking
                    found <- i
                    delta <- d
                    i <- hitData.Length
                i <- i + 1

            if found >= 0 then
                let struct (t, deltas, status) = hitData.[found]
                status.[k] <- HitStatus.RELEASE_ACCEPTED
                deltas.[k] <- delta / rate
                this._HandleEvent { Time = relativeTime; Column = k; Guts = Release_ (deltas.[k], false, false, fst internalHoldStates.[k] = Dropped) }
                internalHoldStates.[k] <- Nothing, found
            else // If we released but too early (no sign of the tail within range) make the long note dropped
                internalHoldStates.[k] <- 
                    match internalHoldStates.[k] with
                    | Holding, i -> Dropped, i
                    | x -> x
        | MissedHead, _
        | Nothing, _ -> ()
    
    abstract member HandleEvent : HitEvent<HitEventGutsInternal> -> HitEvent<HitEventGuts>
    member private this._HandleEvent ev =
        let ev = this.HandleEvent ev
        hitEvents.Add ev
        hitCallback ev
        healthBar.HandleEvent ev

module DP_Utils =

    let windows judge ridiculous =
        let pf = 45.0f<ms> / 6.0f * (10.0f - (judge |> float32))

        let ma = pf * 0.5f
        let gr = pf * 2f
        let gd = pf * 3f |> min 135.0f<ms>
        let bd = pf * 4f |> min 180.0f<ms>

        let rd = pf * 0.25f

        if ridiculous then
            [
                -bd, 6; -gd, 5; -gr, 4; -pf, 3; -ma, 2; -rd, 1
                rd, 0; ma, 1; pf, 2; gr, 3; gd, 4; bd, 5
            ]
        else
            [
                -bd, 5; -gd, 4; -gr, 3; -pf, 2; -ma, 1
                ma, 0; pf, 1; gr, 2; gd, 3; bd, 4
            ]

module Wife_Utils =
    
    // lifted from https://github.com/etternagame/etterna/blob/0a7bd768cffd6f39a3d84d76964097e43011ce33/Themes/_fallback/Scripts/10%20Scores.lua#L606-L627
    let wife_curve (judge: int) (delta: Time) =
        let erf = 
            // was this really necessary
            let a1 =  0.254829592
            let a2 = -0.284496736
            let a3 =  1.421413741
            let a4 = -1.453152027
            let a5 =  1.061405429
            let p  =  0.3275911
            fun (x: float) ->
                let sign = if x < 0.0 then -1.0 else 1.0
                let x = Math.Abs x
                let t = 1.0 / (1.0 + p * x)
                let y = 1.0 - (((((a5 * t + a4) * t) + a3) * t + a2) * t + a1) * t * Math.Exp(-x * x)
                sign * y

        let delta = float delta |> Math.Abs

        let scale = (10.0 - float judge) / 6.0
        let miss_weight = -2.75
        let ridic = 5.0 * scale
        let boo_window = 180.0 * scale
        let ts_pow = 0.75
        let zero = 65.0 * Math.Pow(scale, ts_pow)
        let dev = 22.7 * Math.Pow(scale, ts_pow)

        if delta <= ridic then 1.0
        elif delta <= zero then erf ((zero - delta) / dev)
        elif delta <= boo_window then (delta - zero) * miss_weight / (boo_window - zero)
        else miss_weight

    let config (judge: int) =
        {
            Name = if judge = 9 then "Wife3 JUSTICE" else sprintf "Wife3 (J%i)" judge
            Judgements =
                [|
                    { Name = "Marvellous"; Color = Color.Aqua; BreaksCombo = false }
                    { Name = "Perfect"; Color = Color.Yellow; BreaksCombo = false }
                    { Name = "Great"; Color = Color.FromArgb(0, 255, 100); BreaksCombo = false }
                    { Name = "Good"; Color = Color.Blue; BreaksCombo = true }
                    { Name = "Bad"; Color = Color.Fuchsia; BreaksCombo = true }
                    { Name = "Miss"; Color = Color.Red; BreaksCombo = true }
                |]
            Accuracy = 
                {
                    MissWindow = 180.0f<ms>
                    CbrushWindow = 180.0f<ms>
                    Timegates = DP_Utils.windows judge false
                    Points = AccuracyPoints.WifeCurve judge
                    HoldNoteBehaviour = HoldNoteBehaviour.JustBreakCombo
                }
            Health =
                {
                    StartingHealth = 0.5
                    OnlyFailAtEnd = false
                    ClearThreshold = 0.0
                    Points = [|0.008; 0.008; 0.004; 0.0; -0.04; -0.08|]
                }
            Grading = 
                {
                    Grades = 
                        [|
                            { Name = "D"; Accuracy = 0.0; Color = Color.Red }
                            { Name = "C"; Accuracy = 0.6; Color = Color.Purple }
                            { Name = "B"; Accuracy = 0.7; Color = Color.Blue }
                            { Name = "A"; Accuracy = 0.8; Color = Color.Lime }
                            { Name = "A."; Accuracy = 0.85; Color = Color.Lime }
                            { Name = "A:"; Accuracy = 0.9; Color = Color.Lime }
                            { Name = "AA"; Accuracy = 0.93; Color = Color.Gold }
                            { Name = "AA."; Accuracy = 0.965; Color = Color.Gold }
                            { Name = "AA:"; Accuracy = 0.99; Color = Color.Gold }
                            { Name = "AAA"; Accuracy = 0.997; Color = Color.Gold }
                            { Name = "AAA."; Accuracy = 0.998; Color = Color.Gold }
                            { Name = "AAA:"; Accuracy = 0.999; Color = Color.Gold }
                            { Name = "AAAA"; Accuracy = 0.99955; Color = Color.Gold }
                            { Name = "AAAA."; Accuracy = 0.9997; Color = Color.Gold }
                            { Name = "AAAA:"; Accuracy = 0.9998; Color = Color.Gold }
                            { Name = "AAAAA"; Accuracy = 0.999935; Color = Color.Gold }
                        |]
                    Lamps =
                        [|
                            { Name = "SDCB"; Judgement = -1; JudgementThreshold = 9; Color = Color.FromArgb(255, 160, 160) }
                            { Name = "MF"; Judgement = -1; JudgementThreshold = 1; Color = Color.FromArgb(160, 160, 160) }
                            { Name = "FC"; Judgement = -1; JudgementThreshold = 0; Color = Color.FromArgb(80, 255, 80) }
                            { Name = "SDG"; Judgement = 2; JudgementThreshold = 9; Color = Color.FromArgb(160, 255, 160) }
                            { Name = "BF"; Judgement = 2; JudgementThreshold = 1; Color = Color.FromArgb(200, 160, 255) }
                            { Name = "PFC"; Judgement = 2; JudgementThreshold = 0; Color = Color.FromArgb(255, 255, 80) }
                            { Name = "SDP"; Judgement = 1; JudgementThreshold = 9; Color = Color.FromArgb(255, 255, 160) }
                            { Name = "WF"; Judgement = 1; JudgementThreshold = 1; Color = Color.FromArgb(255, 160, 255) }
                            { Name = "MFC"; Judgement = 1; JudgementThreshold = 0; Color = Color.FromArgb(160, 255, 255) }
                        |]
                }
        }

module Osu_Utils =
    
    let ln_judgement (od: float32) (headDelta: Time) (endDelta: Time) (overhold: bool) (dropped: bool) : JudgementId =
        let absolute = Time.Abs endDelta * 0.5f
        let headDelta = Time.Abs headDelta

        if
            absolute < 16.5f<ms> * 1.2f &&
            absolute + headDelta < 16.5f<ms> * 2.4f &&
            (overhold || headDelta < 151.5f<ms> - od * 3.0f<ms>) &&
            not dropped
        then 0 // 300g
        elif
            absolute < (64.5f<ms> - od * 3.0f<ms>) * 1.1f &&
            absolute + headDelta < (64.5f<ms> - od * 3.0f<ms>) * 2.2f &&
            (overhold || headDelta < 151.5f<ms> - od * 3.0f<ms>) &&
            not dropped
        then 1 // 300
        elif 
            absolute < 97.5f<ms> - od * 3.0f<ms> &&
            absolute + headDelta < (97.5f<ms> - od * 3.0f<ms>) * 2.0f &&
            (overhold || headDelta < 151.5f<ms> - od * 3.0f<ms>)
        then 2 // 200
        elif
            absolute < 127.5f<ms> - od * 3.0f<ms> &&
            absolute + headDelta < (127.5f<ms> - od * 3.0f<ms>) * 2.0f &&
            (overhold || headDelta < 151.5f<ms> - od * 3.0f<ms>)
        then 3 // 100
        elif
            overhold || headDelta < 151.5f<ms> - od * 3.0f<ms>
        then 4 // 50
        else 5 // MISS

    let windows od =
        let ma = 16.5f<ms>
        let pf = 64.5f<ms> - od * 3.0f<ms>
        let gr = 97.5f<ms> - od * 3.0f<ms>
        let gd = 127.5f<ms> - od * 3.0f<ms>
        let bd = 151.5f<ms> - od * 3.0f<ms>
        [
            -bd, 5; -gd, 4; -gr, 3; -pf, 2; -ma, 1
            ma, 0; pf, 1; gr, 2; gd, 3; bd, 4
        ]

    let config (od: float32) : ScoreSystemConfig =
        {
            Name = sprintf "osu! (OD%.1f)" od
            Judgements =
                [|
                    { Name = "300g"; Color = Color.Aqua; BreaksCombo = false }
                    { Name = "300"; Color = Color.Yellow; BreaksCombo = false }
                    { Name = "200"; Color = Color.FromArgb(0, 255, 100); BreaksCombo = false }
                    { Name = "100"; Color = Color.FromArgb(0, 160, 255); BreaksCombo = false }
                    { Name = "50"; Color = Color.FromArgb(160, 160, 160); BreaksCombo = false }
                    { Name = "MISS"; Color = Color.FromArgb(255, 80, 80); BreaksCombo = true }
                |]
            Accuracy = 
                {
                    MissWindow = 180.0f<ms>
                    CbrushWindow = 180.0f<ms>
                    Timegates = windows od
                    Points = AccuracyPoints.Weights (300.0, [|300.0; 300.0; 200.0; 100.0; 50.0; 0.0|])
                    HoldNoteBehaviour = HoldNoteBehaviour.Osu od
                }
            Health =
                {
                    StartingHealth = 1.0
                    OnlyFailAtEnd = false
                    ClearThreshold = 0.0
                    // Roughly HP8
                    Points = [|0.008; 0.008; 0.004; 0.0; -0.033; -0.066|]
                }
            Grading = 
                {
                    Grades = 
                        [|
                            { Name = "D"; Accuracy = 0.0; Color = Color.FromArgb(255, 80, 80) }
                            { Name = "C"; Accuracy = 0.7; Color = Color.FromArgb(255, 80, 255) }
                            { Name = "B"; Accuracy = 0.8; Color = Color.FromArgb(0, 80, 255) }
                            { Name = "A"; Accuracy = 0.9; Color = Color.FromArgb(0, 255, 100) }
                            { Name = "S"; Accuracy = 0.95; Color = Color.FromArgb(246, 234, 128) }
                            { Name = "SS"; Accuracy = 1.0; Color = Color.FromArgb(255, 255, 160) }
                        |]
                    Lamps =
                        [|
                            { Name = "SDCB"; Judgement = 5; JudgementThreshold = 9; Color = Color.FromArgb(255, 160, 160) }
                            { Name = "FC"; Judgement = 5; JudgementThreshold = 0; Color = Color.FromArgb(0, 255, 160) }
                            { Name = "PFC"; Judgement = 3; JudgementThreshold = 0; Color = Color.FromArgb(255, 255, 160) }
                            { Name = "MFC"; Judgement = 1; JudgementThreshold = 0; Color = Color.FromArgb(160, 255, 255) }
                        |]
                }
        }

module SC_Utils =

    // not used at the moment
    let sc_curve (judge: int) (isRelease: bool) (delta: Time) =
        let delta = Time.Abs delta

        // 1.0 = 100%
        if delta >= 180.0f<ms> then -1.0
        else
            let delta = if isRelease then delta * 0.5f else delta
            let delta = float delta
            let scale = 6.0 / (10.0 - float judge)
            Math.Max(-1.0, (1.0 - Math.Pow(delta * scale, 2.8) * 0.0000056))

    let config (judge: int) =
        {
            Name = sprintf "SC (J%i)" judge
            Judgements =
                [|
                    { Name = "Marvellous"; Color = Color.Aqua; BreaksCombo = false }
                    { Name = "Perfect"; Color = Color.Yellow; BreaksCombo = false }
                    { Name = "Great"; Color = Color.FromArgb(0, 255, 100); BreaksCombo = false }
                    { Name = "Good"; Color = Color.Blue; BreaksCombo = true }
                    { Name = "Bad"; Color = Color.Fuchsia; BreaksCombo = true }
                    { Name = "Miss"; Color = Color.Red; BreaksCombo = true }
                |]
            Accuracy = 
                {
                    MissWindow = 180.0f<ms>
                    CbrushWindow = 90.0f<ms>
                    Timegates = DP_Utils.windows judge false
                    Points = AccuracyPoints.Weights (10.0, [|10.0; 9.0; 5.0; -5.0; -10.0; -10.0|])
                    HoldNoteBehaviour = HoldNoteBehaviour.Normal {| JudgementIfDropped = 3; JudgementIfOverheld = 3 |}
                }
            Health =
                {
                    StartingHealth = 0.5
                    OnlyFailAtEnd = false
                    ClearThreshold = 0.0
                    Points = [|0.008; 0.008; 0.004; 0.0; -0.04; -0.08|]
                }
            Grading = 
                {
                    Grades = 
                        [|
                            { Name = "F"; Accuracy = 0.0; Color = Color.FromArgb(200, 163, 155) }
                            { Name = "D"; Accuracy = 0.89995; Color = Color.FromArgb(194, 162, 182) }
                            { Name = "C"; Accuracy = 0.90995; Color = Color.FromArgb(202, 153, 183) }
                            { Name = "C+"; Accuracy = 0.91995; Color = Color.FromArgb(163, 190, 207) }
                            { Name = "B"; Accuracy = 0.92995; Color = Color.FromArgb(149, 193, 220) }
                            { Name = "B+"; Accuracy = 0.93995; Color = Color.FromArgb(148, 210, 180) }
                            { Name = "A"; Accuracy = 0.94995; Color = Color.FromArgb(134, 227, 183) }
                            { Name = "A+"; Accuracy = 0.95995; Color = Color.FromArgb(127, 231, 139) }
                            { Name = "S-"; Accuracy = 0.96995; Color = Color.FromArgb(237, 205, 140) }
                            { Name = "S"; Accuracy = 0.97995; Color = Color.FromArgb(246, 234, 128) }
                            { Name = "S+"; Accuracy = 0.98995; Color = Color.FromArgb(235, 200, 220) }
                        |]
                    Lamps =
                        [|
                            { Name = "SDCB"; Judgement = -1; JudgementThreshold = 9; Color = Color.FromArgb(255, 160, 160) }
                            { Name = "MF"; Judgement = -1; JudgementThreshold = 1; Color = Color.FromArgb(160, 160, 160) }
                            { Name = "FC"; Judgement = -1; JudgementThreshold = 0; Color = Color.FromArgb(80, 255, 80) }
                            { Name = "SDG"; Judgement = 2; JudgementThreshold = 9; Color = Color.FromArgb(160, 255, 160) }
                            { Name = "BF"; Judgement = 2; JudgementThreshold = 1; Color = Color.FromArgb(200, 160, 255) }
                            { Name = "PFC"; Judgement = 2; JudgementThreshold = 0; Color = Color.FromArgb(255, 255, 80) }
                            { Name = "SDP"; Judgement = 1; JudgementThreshold = 9; Color = Color.FromArgb(255, 255, 160) }
                            { Name = "WF"; Judgement = 1; JudgementThreshold = 1; Color = Color.FromArgb(255, 160, 255) }
                            { Name = "MFC"; Judgement = 1; JudgementThreshold = 0; Color = Color.FromArgb(160, 255, 255) }
                        |]
                }
        }

module Ex_Score_Utils =

    type ExScoreType =
        {
            Name: string
            Critical: Time
            Near: Time
            MissWindow: Time
        }

    let sdvx : ExScoreType =
        {
            Name = "SDVX"
            Critical = 50.0f<ms>
            Near = 150.0f<ms>
            MissWindow = 150.0f<ms>
        }

    let config (data: ExScoreType) =
        {
            Name = sprintf "EXSCORE (%s)" data.Name
            Judgements =
                [|
                    { Name = "CRITICAL"; Color = Color.Aqua; BreaksCombo = false }
                    { Name = "NEAR"; Color = Color.Yellow; BreaksCombo = false }
                    { Name = "BREAK"; Color = Color.FromArgb(0, 255, 100); BreaksCombo = true }
                    { Name = "MISS"; Color = Color.FromArgb(255, 100, 100); BreaksCombo = true }
                |]
            Accuracy = 
                {
                    MissWindow = 150.0f<ms>
                    CbrushWindow = 150.0f<ms>
                    Timegates = [-data.Critical, 1; data.Critical, 0; data.Near, 1]
                    Points = AccuracyPoints.Weights (1.0, [|1.0; 0.5; 0.0; 0.0|])
                    HoldNoteBehaviour = HoldNoteBehaviour.Normal {| JudgementIfDropped = 2; JudgementIfOverheld = 1 |}
                }
            Health =
                {
                    StartingHealth = 0.5
                    OnlyFailAtEnd = false
                    ClearThreshold = 0.0
                    Points = [|0.008; 0.008; 0.004; 0.0; -0.04; -0.08|]
                }
            Grading = 
                {
                    Grades = 
                        [|
                            { Name = "B"; Accuracy = 0.90; Color = Color.FromArgb(202, 153, 183) }
                            { Name = "B+"; Accuracy = 0.92; Color = Color.FromArgb(163, 190, 207) }
                            { Name = "A"; Accuracy = 0.93; Color = Color.FromArgb(149, 193, 220) }
                            { Name = "A+"; Accuracy = 0.94; Color = Color.FromArgb(148, 210, 180) }
                            { Name = "AA"; Accuracy = 0.95; Color = Color.FromArgb(134, 227, 183) }
                            { Name = "AA+"; Accuracy = 0.96; Color = Color.FromArgb(127, 231, 139) }
                            { Name = "AAA"; Accuracy = 0.97; Color = Color.FromArgb(237, 205, 140) }
                            { Name = "AAA+"; Accuracy = 0.98; Color = Color.FromArgb(246, 234, 128) }
                            { Name = "S"; Accuracy = 0.99; Color = Color.FromArgb(235, 200, 220) }
                        |]
                    Lamps =
                        [|
                            { Name = "PFC"; Judgement = 1; JudgementThreshold = 0; Color = Color.FromArgb(255, 255, 80) }
                            { Name = "SDP"; Judgement = 1; JudgementThreshold = 9; Color = Color.FromArgb(255, 255, 160) }
                            { Name = "1P"; Judgement = 1; JudgementThreshold = 1; Color = Color.FromArgb(255, 255, 200) }
                            { Name = "UFC"; Judgement = 1; JudgementThreshold = 0; Color = Color.FromArgb(160, 255, 255) }
                        |]
                }
        }

module Helpers =

    let window_func (default_judge: JudgementId) (gates: (Time * JudgementId) list) (delta: Time) : JudgementId =
        let rec loop gates =
            match gates with
            | [] -> default_judge
            | (w, j) :: xs ->
                if delta < w then j else loop xs
        loop gates

    let points (conf: ScoreSystemConfig) (delta: Time) (judge: JudgementId) : float =
        match conf.Accuracy.Points with
        | AccuracyPoints.WifeCurve j -> Wife_Utils.wife_curve j delta
        | AccuracyPoints.Weights (maxweight, weights) -> weights.[judge] / maxweight

// Concrete implementation of rulesets

type CustomScoring(config: ScoreSystemConfig, keys, replay, notes, rate) =
    inherit IScoreMetric(config, HealthBarMetric(config.Health), keys, replay, notes, rate)

    let headJudgements = Array.create keys config.DefaultJudgement
    let headDeltas = Array.create keys config.Accuracy.MissWindow

    let point_func = Helpers.points config
    let window_func = Helpers.window_func config.DefaultJudgement config.Accuracy.Timegates

    override this.HandleEvent ev =
        { 
            Time = ev.Time
            Column = ev.Column
            Guts = 
                match ev.Guts with
                | Hit_ (delta, isHold, missed) ->
                    let judgement = window_func delta
                    if isHold then
                        headJudgements.[ev.Column] <- judgement
                        headDeltas.[ev.Column] <- delta

                        match config.Accuracy.HoldNoteBehaviour with
                        | HoldNoteBehaviour.JustBreakCombo
                        | HoldNoteBehaviour.JudgeReleases _ -> 
                            this.State.Add(point_func delta judgement, 1.0, judgement)
                            if config.Judgements.[judgement].BreaksCombo then this.State.BreakCombo true else this.State.IncrCombo()
                            Hit {| Judgement = Some judgement; Missed = missed; Delta = delta; IsHold = isHold |}

                        | HoldNoteBehaviour.Osu _
                        | HoldNoteBehaviour.Normal _
                        | HoldNoteBehaviour.OnlyJudgeReleases ->
                            Hit {| Judgement = None; Missed = missed; Delta = delta; IsHold = isHold |}
                    else
                        this.State.Add(point_func delta judgement, 1.0, judgement)
                        if config.Judgements.[judgement].BreaksCombo then this.State.BreakCombo true else this.State.IncrCombo()
                        Hit {| Judgement = Some judgement; Missed = missed; Delta = delta; IsHold = isHold |}

                | Release_ (delta, missed, overhold, dropped) ->
                    let headJudgement = headJudgements.[ev.Column]

                    match config.Accuracy.HoldNoteBehaviour with
                    | HoldNoteBehaviour.Osu od ->
                        let judgement = Osu_Utils.ln_judgement od headDeltas.[ev.Column] delta overhold dropped
                        this.State.Add(point_func delta judgement, 1.0, judgement)
                        if config.Judgements.[judgement].BreaksCombo then this.State.BreakCombo true else this.State.IncrCombo()
                        Release {| Judgement = Some judgement; Missed = missed; Delta = delta; Overhold = overhold; Dropped = dropped |}

                    | HoldNoteBehaviour.JustBreakCombo ->
                        if missed || dropped then this.State.BreakCombo true else this.State.IncrCombo()
                        Release {| Judgement = None; Missed = missed; Delta = delta; Overhold = overhold; Dropped = dropped |}

                    | HoldNoteBehaviour.JudgeReleases d -> 
                        let judgement = Helpers.window_func config.DefaultJudgement d.Timegates delta
                        this.State.Add(point_func delta judgement, 1.0, judgement)
                        if config.Judgements.[judgement].BreaksCombo then this.State.BreakCombo true else this.State.IncrCombo()
                        Release {| Judgement = Some judgement; Missed = missed; Delta = delta; Overhold = overhold; Dropped = dropped |}

                    | HoldNoteBehaviour.Normal rules ->
                        let judgement =
                            if missed || dropped then max headJudgement rules.JudgementIfDropped
                            elif overhold then max headJudgement rules.JudgementIfOverheld
                            else headJudgement
                        this.State.Add(point_func delta judgement, 1.0, judgement)
                        if config.Judgements.[judgement].BreaksCombo then this.State.BreakCombo true else this.State.IncrCombo()
                        Release {| Judgement = Some judgement; Missed = missed; Delta = delta; Overhold = overhold; Dropped = dropped |}

                    | HoldNoteBehaviour.OnlyJudgeReleases ->
                        let judgement = window_func delta
                        this.State.Add(point_func delta judgement, 1.0, judgement)
                        if config.Judgements.[judgement].BreaksCombo then this.State.BreakCombo true else this.State.IncrCombo()
                        Release {| Judgement = Some judgement; Missed = missed; Delta = delta; Overhold = overhold; Dropped = dropped |}
        }

module Metrics =
    
    let createScoreMetric config keys (replay: IReplayProvider) notes rate : IScoreMetric =
        CustomScoring(config, keys, replay, notes, rate)

    let createDummyMetric (chart: Chart) : IScoreMetric =
        createScoreMetric (SC_Utils.config 4) chart.Keys (StoredReplayProvider Array.empty) chart.Notes 1.0f