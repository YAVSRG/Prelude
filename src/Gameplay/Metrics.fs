﻿namespace Prelude.Gameplay

open System
open Prelude
open Prelude.Charts.Formats.Interlude

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
    Accuracy/scoring system metric.
    Each note you hit is assigned a certain number of points - Your % accuracy is points scored out of the possible maximum.
    Combo/combo breaking also built-in - Your combo is the number of notes hit well in a row
*)

[<Struct>]
type private HoldInternalState =
    | Nothing
    | Holding
    | Dropped
    | MissedHeadThenHeld
    | MissedHead

[<Struct>]
[<RequireQualifiedAccess>]
type HoldState =
    | Released
    | Holding
    | Dropped
    | MissedHead
    | InTheFuture
    member this.ShowInReceptor = this = Holding || this = Dropped || this = Released


[<Struct>]
type ScoreMetricSnapshot =
    {
        Time: ChartTime
        PointsScored: float
        MaxPointsScored: float
        Combo: int
        Lamp: int
    }
    static member COUNT = 100

[<AbstractClass>]
type IScoreMetric
    (
        ruleset: Ruleset,
        keys: int,
        replayProvider: IReplayProvider,
        notes: TimeArray<NoteRow>,
        rate: float32
    ) =
    inherit ReplayConsumer(keys, replayProvider)

    let firstNote = (TimeArray.first notes).Value.Time
    let lastNote = (TimeArray.last notes).Value.Time
    let duration = lastNote - firstNote
    let missWindow = ruleset.Accuracy.MissWindow * rate

    // having two seekers improves performance when feeding scores rather than playing live
    let mutable noteSeekPassive = 0
    let mutable noteSeekActive = 0

    let internalHoldStates = Array.create keys (Nothing, -1)

    let snapshots = ResizeArray<ScoreMetricSnapshot>()
    let hitData = InternalScore.createDefault ruleset.Accuracy.MissWindow keys notes
    let hitEvents = ResizeArray<HitEvent<HitEventGuts>>()

    let onHit = Event<HitEvent<HitEventGuts>>()
    let onHit_Published = onHit.Publish

    member this.OnHit = onHit_Published

    member val State =
        {
            Judgements = Array.zeroCreate ruleset.Judgements.Length
            PointsScored = 0.0
            MaxPointsScored = 0.0
            CurrentCombo = 0
            BestCombo = 0
            ComboBreaks = 0
            MaxPossibleCombo = 0
        }

    member this.Name = ruleset.Name
    member this.Value =
        let v = this.State.PointsScored / this.State.MaxPointsScored
        if Double.IsNaN v then 1.0 else v
    member this.FormatAccuracy() = sprintf "%.2f%%" (this.Value * 100.0)
    member this.MissWindow = ruleset.Accuracy.MissWindow
    member this.ScaledMissWindow = missWindow
    member this.Ruleset = ruleset

    member this.HoldState (index: int) (k: int) =
        let state, i = internalHoldStates.[k]
        if i = index then
            match state with
            | Nothing -> HoldState.Released
            | Holding -> HoldState.Holding
            | Dropped -> HoldState.Dropped
            | MissedHead | MissedHeadThenHeld -> HoldState.MissedHead
        elif i > index then
            let struct (_, _, flags) = hitData.[index]
            if flags.[k] <> HitStatus.HIT_HOLD_REQUIRED then HoldState.Released else HoldState.MissedHead
        else HoldState.InTheFuture

    member this.IsNoteHit (index: int) (k: int) =
        let struct (_, _, flags) = hitData.[index]
        flags.[k] = HitStatus.HIT_ACCEPTED

    member this.HitData = hitData

    member this.Finished = noteSeekPassive = hitData.Length

    member this.HitEvents = hitEvents.AsReadOnly()
    member this.Snapshots = snapshots.AsReadOnly()

    // correctness guaranteed up to the time you update, no matter how you update
    // call Update with Time.infinity to do a correct feeding of the whole replay
    member this.Update (relativeTime: ChartTime) =
        this.PollReplay relativeTime // calls HandleKeyDown and HandleKeyUp appropriately
        this.HandlePassive relativeTime

    member private this.HandlePassive (relativeTime: ChartTime) =
        let now = firstNote + relativeTime
        let target = now - missWindow
        let snapshot_target_count = (float32 ScoreMetricSnapshot.COUNT * relativeTime) / duration |> ceil |> int |> max 0 |> min ScoreMetricSnapshot.COUNT
        while snapshots.Count < snapshot_target_count do
            snapshots.Add
                { 
                    Time = relativeTime
                    PointsScored = this.State.PointsScored
                    MaxPointsScored = this.State.MaxPointsScored
                    Combo = this.State.CurrentCombo
                    Lamp = Lamp.calculate ruleset.Grading.Lamps this.State
                }
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
                        | Dropped, i | Holding, i when i <= noteSeekPassive -> Bitmask.hasBit k this.KeyState
                        | _ -> false
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
        let mutable cbrush_absorb_delta = missWindow
        let mutable earliest_note = -1
        let mutable earliest_delta = missWindow
        let target = now + missWindow

        while i < hitData.Length && InternalScore.offsetOf hitData.[i] <= target do
            let struct (t, deltas, status) = hitData.[i]
            let d = now - t
            if (status.[k] = HitStatus.HIT_REQUIRED || status.[k] = HitStatus.HIT_HOLD_REQUIRED) then
                if (Time.abs earliest_delta > Time.abs d) then
                    earliest_note <- i
                    earliest_delta <- d
                if Time.abs earliest_delta < ruleset.Accuracy.CbrushWindow then
                    i <- hitData.Length
            // Detect a hit that looks like it's intended for a previous badly hit note that was fumbled early (preventing column lock)
            elif status.[k] = HitStatus.HIT_ACCEPTED && deltas.[k] < -ruleset.Accuracy.CbrushWindow then
                if (Time.abs cbrush_absorb_delta > Time.abs d) then
                    cbrush_absorb_delta <- d
            i <- i + 1

        if earliest_note >= 0 then
            let struct (_, deltas, status) = hitData.[earliest_note]
            // If user's hit is closer to a note hit extremely early than any other note, swallow it
            if Time.abs cbrush_absorb_delta >= Time.abs earliest_delta then
                let isHoldHead = status.[k] <> HitStatus.HIT_REQUIRED
                status.[k] <- HitStatus.HIT_ACCEPTED
                deltas.[k] <- earliest_delta / rate
                this._HandleEvent { Time = relativeTime; Column = k; Guts = Hit_ (deltas.[k], isHoldHead, false) }
                // Begin tracking if it's a hold note
                if isHoldHead then internalHoldStates.[k] <- Holding, earliest_note
        else // If no note to hit, but a hold note head was missed, pressing key marks it dropped instead
            internalHoldStates.[k] <- 
                match internalHoldStates.[k] with
                | MissedHead, i -> MissedHeadThenHeld, i
                | x -> x
                
    override this.HandleKeyUp (relativeTime: ChartTime, k: int) =
        this.HandlePassive relativeTime
        let now = firstNote + relativeTime
        match internalHoldStates.[k] with
        | Holding, holdHeadIndex
        | Dropped, holdHeadIndex
        | MissedHeadThenHeld, holdHeadIndex ->

            let mutable i = holdHeadIndex
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
                let struct (_, deltas, status) = hitData.[found]
                status.[k] <- HitStatus.RELEASE_ACCEPTED
                deltas.[k] <- delta / rate
                this._HandleEvent { Time = relativeTime; Column = k; Guts = Release_ (deltas.[k], false, false, fst internalHoldStates.[k] = Dropped || fst internalHoldStates.[k] = MissedHeadThenHeld) }
                internalHoldStates.[k] <- Nothing, holdHeadIndex
            else // If we released but too early (no sign of the tail within range) make the long note dropped
                internalHoldStates.[k] <- 
                    match internalHoldStates.[k] with
                    | Holding, i -> Dropped, i
                    | x -> x
                match ruleset.Accuracy.HoldNoteBehaviour with HoldNoteBehaviour.Osu _ -> this.State.BreakCombo(false) | _ -> ()
        | MissedHead, _
        | Nothing, _ -> ()
    
    abstract member HandleEvent : HitEvent<HitEventGutsInternal> -> HitEvent<HitEventGuts>
    member private this._HandleEvent ev =
        let ev = this.HandleEvent ev
        hitEvents.Add ev
        onHit.Trigger ev

module Helpers =

    let window_func (default_judge: JudgementId) (gates: (Time * JudgementId) list) (delta: Time) : JudgementId =
        let rec loop gates =
            match gates with
            | [] -> default_judge
            | (w, j) :: xs ->
                if delta < w then j else loop xs
        loop gates

    let points (conf: Ruleset) (delta: Time) (judge: JudgementId) : float =
        match conf.Accuracy.Points with
        | AccuracyPoints.WifeCurve j -> RulesetUtils.wife_curve j delta
        | AccuracyPoints.Weights (maxweight, weights) -> weights.[judge] / maxweight

// Concrete implementation of rulesets

type ScoreMetric(config: Ruleset, keys, replay, notes, rate) =
    inherit IScoreMetric(config, keys, replay, notes, rate)

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
                        let judgement = RulesetUtils.osu_ln_judgement od headDeltas.[ev.Column] delta overhold dropped
                        this.State.Add(point_func delta judgement, 1.0, judgement)
                        if config.Judgements.[judgement].BreaksCombo then this.State.BreakCombo true else this.State.IncrCombo()
                        Release {| Judgement = Some judgement; Missed = missed; Delta = delta; Overhold = overhold; Dropped = dropped |}

                    | HoldNoteBehaviour.JustBreakCombo ->
                        if (not overhold) && (missed || dropped) then this.State.BreakCombo true else this.State.IncrCombo()
                        Release {| Judgement = None; Missed = missed; Delta = delta; Overhold = overhold; Dropped = dropped |}

                    | HoldNoteBehaviour.JudgeReleases d -> 
                        let judgement = Helpers.window_func config.DefaultJudgement d.Timegates delta
                        this.State.Add(point_func delta judgement, 1.0, judgement)
                        if config.Judgements.[judgement].BreaksCombo then this.State.BreakCombo true else this.State.IncrCombo()
                        Release {| Judgement = Some judgement; Missed = missed; Delta = delta; Overhold = overhold; Dropped = dropped |}

                    | HoldNoteBehaviour.Normal rules ->
                        let judgement =
                            if overhold && not dropped then max headJudgement rules.JudgementIfOverheld
                            elif missed || dropped then max headJudgement rules.JudgementIfDropped
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
    
    let createScoreMetric ruleset keys (replay: IReplayProvider) notes rate : ScoreMetric =
        ScoreMetric(ruleset, keys, replay, notes, rate)

    let createDummyMetric (chart: Chart) : ScoreMetric =
        let ruleset = PrefabRulesets.SC.create 4
        createScoreMetric 
            ruleset
            chart.Keys
            (StoredReplayProvider Array.empty)
            chart.Notes
            1.0f