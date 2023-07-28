namespace Prelude.Charts.Formats.Conversions

open System
open System.Text.RegularExpressions
open System.IO
open System.Collections.Generic
open System.Linq
open Percyqaz.Common
open Prelude
open Prelude.Charts.Formats.``osu!``
open Prelude.Charts.Formats.Interlude
open Prelude.Charts.Formats.StepMania

type ConversionActionConfig =
    {
        CopyMediaFiles: bool
        StepmaniaPackId: int option
        ChangedAfter: DateTime option
        PackName: string
    }
    static member Default =
        {
            CopyMediaFiles = true
            StepmaniaPackId = None
            ChangedAfter = None
            PackName = "Singles"
        }

type ConversionAction =
    {
        Config: ConversionActionConfig
        Source: string
    }

module ``osu!`` =

    let private convertHitObjects (objects: HitObject list) (keys: int) : TimeArray<NoteRow> =
        let holds = Array.create keys -1.0f<ms>

        let xToColumn (x: float) = x / 512.0 * float keys |> int |> min (keys - 1) |> max 0

        let createRow () =
            let nr = NoteRow.createEmpty keys
            for i = 0 to (holds.Length - 1) do
                if holds.[i] >= 0.0f<ms> then nr.[i] <- NoteType.HOLDBODY
            nr

        let updateHolds time (snaps: TimeItem<NoteRow> list) =
            let mutable s = snaps
            let mutable minT = Array.fold (fun y x -> if x = -1.0f<ms> then y else min y x) Time.infinity holds
            while minT < time do
                for k in 0..(keys - 1) do
                    if (holds.[k] >= 0.0f<ms> && holds.[k] = minT) then 
                        match s with
                        | { Time = t; Data = nr } :: ss ->
                            holds.[k] <- -1.0f<ms>
                            if time = minT then
                                nr.[k] <- NoteType.HOLDTAIL
                            else s <- { Time = minT; Data = let nr = createRow() in nr.[k] <- NoteType.HOLDTAIL; nr } :: s
                        | [] -> failwith "impossible"
                minT <- Array.fold (fun y x -> if x = -1.0f<ms> then y else min y x) Time.infinity holds
            s

        let note k time (snaps: TimeItem<NoteRow> list) =
            if holds.[k] <> -1.0f<ms> then
                //Logging.Debug(sprintf "Skipping stacked note (inside LN) at %f" time)
                snaps
            else

            match snaps with
            | { Time = t; Data = nr } :: ss ->
                if t = time then 
                    if nr.[k] <> NoteType.NOTHING then 
                        ()//Logging.Debug(sprintf "Skipping stacked note at %f" time)
                    else nr.[k] <- NoteType.NORMAL
                    snaps
                else { Time = time; Data = let row = createRow() in row.[k] <- NoteType.NORMAL; row } :: snaps
            | [] -> { Time = time; Data = let row = createRow() in row.[k] <- NoteType.NORMAL; row } :: snaps

        let hold (k: int) (time: Time) (release: Time) (snaps: TimeItem<NoteRow> list) =
            if holds.[k] <> -1.0f<ms> then 
                //Logging.Debug(sprintf "Skipping stacked LN (inside LN) at %f" time)
                snaps
            elif release < time then 
                //Logging.Debug(sprintf "Skipping LN that ends at %f but starts at %f" release time)
                snaps
            elif release = time then
                //Logging.Debug(sprintf "Treating LN that ends & starts at %f as a note" time)
                note k time snaps
            else

            holds.[k] <- release
            match snaps with
            | { Time = t; Data = nr } :: ss ->
                if t = time then nr.[k] <- NoteType.HOLDHEAD; snaps
                else { Time = time; Data = let row = createRow() in row.[k] <- NoteType.HOLDHEAD; row } :: snaps
            | [] -> { Time = time; Data = let row = createRow() in row.[k] <- NoteType.HOLDHEAD; row } :: snaps

        let f (snaps: TimeItem<NoteRow> list) (hitObj: HitObject) =
            match hitObj with
            | HitCircle ((x, y), time, _, _) -> note (xToColumn x) time (updateHolds time snaps)
            | HoldNote ((x, y), time, endTime, _, _) -> hold (xToColumn x) time endTime (updateHolds time snaps)
            | _ -> snaps

        let states = updateHolds Time.infinity (List.fold f [] objects)
        states |> Array.ofList |> Array.rev

    let private convertTimingPoints (points: TimingPoint list) (keys: int) (endTime: Time) : TimeArray<BPM> * TimeArray<float32> =
        let rec bpmDurations points = 
            if List.isEmpty points then failwith "no bpm point"
            match List.head points with
            | TimingPoint.BPM (offset, msPerBeat, _, _, _) ->
                let mutable current: float32<ms/beat> = msPerBeat
                let mutable t: Time = offset
                let data = new Dictionary<float32<ms/beat>, Time>()

                for p in points do
                    if (not (data.ContainsKey current)) then data.Add(current, 0.0f<ms>)
                    match p with
                    | (TimingPoint.SV _) -> ()
                    | (TimingPoint.BPM (offset, msPerBeat, _, _, _)) -> data.[current] <- data.[current] + offset - t; t <- offset; current <- msPerBeat
                if (not (data.ContainsKey current)) then data.Add(current, 0.0f<ms>)
                data.[current] <- data.[current] + endTime - t
                data
            | _ -> bpmDurations (List.tail points)

        let mostCommonBpm = (bpmDurations points).OrderByDescending(fun p -> p.Value).First().Key

        let addOrSkipSV (offset, value) sv =
            match sv with
            | [] -> [{ Time = offset; Data = value }]
            | { Time = time; Data = oldValue } :: s -> if oldValue = value then sv else { Time = offset; Data = value } :: sv

        let (bpm, sv, _) = 
            let func ((bpm, sv, scroll): (TimeItem<BPM> list * TimeItem<float32> list * float32)) (point: TimingPoint) : (TimeItem<BPM> list * TimeItem<float32> list * float32) =
                match point with
                | (TimingPoint.BPM (offset, msPerBeat, meter, _, _)) -> 
                    { Time = offset; Data = { Meter = meter; MsPerBeat = msPerBeat } } :: bpm,
                    addOrSkipSV (offset, (mostCommonBpm / msPerBeat)) sv,
                    mostCommonBpm / msPerBeat
                | (TimingPoint.SV (offset, value, _, _)) ->
                    bpm, 
                    addOrSkipSV (offset, (value * scroll)) sv, 
                    scroll
            List.fold func ([], [], 1.0f) points

        bpm |> Array.ofList |> Array.rev,
        sv |> Array.ofList |> Array.rev

    let private rateRegex = Regex("""((^|\s)([02][,.][0-9][0-9]?|1[,.]0[1-9]|1[,.][1-9][0-9]?)($|\s))|(x([02][,.][0-9][0-9]?|1[,.]0[1-9]|1[,.][1-9][0-9]?))|(([02][,.][0-9][0-9]?|1[,.]0[1-9]|1[,.][1-9][0-9]?)x)""");
    let looks_like_a_rate (b: Beatmap) : bool = rateRegex.IsMatch b.Metadata.Version

    let toInterlude (b: Beatmap) (action: ConversionAction) : Chart =
        let keys = b.Difficulty.CircleSize |> int
        let rec findBackgroundFile e =
            match e with
            | (Background (path, _)) :: _ -> path
            | _ :: es -> findBackgroundFile es
            | [] -> ""
        let header =
            {
                Title = b.Metadata.Title.Trim()
                TitleNative = let t = b.Metadata.TitleUnicode.Trim() in if t.Length > 0 && t <> b.Metadata.Title.Trim() then Some t else None
                Artist = b.Metadata.Artist.Trim()
                ArtistNative = let t = b.Metadata.ArtistUnicode.Trim() in if t.Length > 0 && t <> b.Metadata.Artist.Trim() then Some t else None
                Creator = b.Metadata.Creator
                DiffName = b.Metadata.Version
                Subtitle = None
                Source = let t = b.Metadata.Source.Trim() in if t.Length > 0 then Some t else None
                Tags = b.Metadata.Tags

                PreviewTime = b.General.PreviewTime
                BackgroundFile = findBackgroundFile b.Events |> Relative
                AudioFile = b.General.AudioFilename |> Relative

                ChartSource = Osu (b.Metadata.BeatmapSetID, b.Metadata.BeatmapID)
            }
        let snaps = convertHitObjects b.Objects keys
        if snaps.Length = 0 then failwith "osu! chart has no notes"
        let bpm, sv = convertTimingPoints b.Timing keys (TimeArray.last snaps).Value.Time
        {
            Keys = keys
            Header = header
            Notes = snaps
            BPM = bpm
            SV = sv

            LoadedFromPath = String.Join("_", (b.Metadata.Title + " [" + b.Metadata.Version + "].yav").Split(Path.GetInvalidFileNameChars()))
        }

module Stepmania =

    let private convert_measures (keys: int) (measures: string list list) (bpms: (float32<beat> * float32<beat/minute>) list) start = 

        let mutable bpms = bpms
        let meter = 4<beat>
        let fmeter = float32 meter * 1.0f<beat>
        let states = new List<TimeItem<NoteRow>>()
        let points = new List<TimeItem<BPM>>()
        let mutable ln: Bitmask = 0us
        let mutable now = start
        let (_, b) = List.head bpms in points.Add({ Time = start; Data = { Meter = meter; MsPerBeat = 60000.0f<ms/minute> / b } })
        let mutable msPerBeat = 60000.0f<ms/minute> / b
        bpms <- List.tail bpms
        let mutable totalBeats = 0.0f<beat>
        let mutable lo = 0.0f<beat>
        let mutable hi = 0.0f<beat>

        let convert_measure (m: string list) (lo: float32<beat>) (hi: float32<beat>) =
            let l = List.length m |> float32
            let sep = msPerBeat * fmeter / l
            let start = Math.Ceiling(lo * l / fmeter |> float) |> int
            let finish = Math.Ceiling(hi * l / fmeter |> float) |> int
            let offset = now + (float32 start * sep) - (lo * msPerBeat)

            for i in start .. (finish - 1) do
                let nr = NoteRow.createLnBodies keys ln
                Seq.iteri (fun k c ->
                    match c with
                    | '0' -> ()
                    | '1' -> nr.[k] <- NoteType.NORMAL
                    | '2' | '4' ->
                        nr.[k] <- NoteType.HOLDHEAD
                        ln <- Bitmask.setBit k ln
                    | '3' ->
                        nr.[k] <- NoteType.HOLDTAIL
                        ln <- Bitmask.unsetBit k ln
                    | 'M' | 'L' | 'F' -> () //ignore mines, lifts, fakes
                    | _ -> failwith ("unknown note type " + c.ToString())
                    ) m.[i]
                if NoteRow.isEmpty nr |> not then states.Add({ Time = offset + float32 (i - start) * sep; Data = nr })

        List.iteri (fun i m -> 
            totalBeats <- totalBeats + fmeter
            lo <- 0.0f<beat>
            while ((not (List.isEmpty bpms)) && fst (List.head bpms) < totalBeats) do
                hi <- fst (List.head bpms) - totalBeats + fmeter
                convert_measure m lo hi
                now <- now + msPerBeat * (hi - lo)
                lo <- hi
                let (_, b) = List.head bpms in points.Add({ Time = now; Data = { Meter = meter; MsPerBeat = 60000.0f<ms/minute> / b } })
                msPerBeat <- 60000.0f<ms/minute> / b
                bpms <- List.tail bpms
            convert_measure m lo fmeter
            now <- now + msPerBeat * (fmeter - lo)
            ) measures
        (states |> Array.ofSeq, points |> Array.ofSeq)

    let toInterlude (sm: StepmaniaData) (action: ConversionAction) = 

        let path = Path.GetDirectoryName action.Source

        if sm.STOPS <> [] then 
            Logging.Warn(sprintf "StepMania STOPS are not supported, this file (%s) will not convert correctly" path)
            Logging.Debug(sprintf "Stops: %A" sm.STOPS)

        let rec metadataFallback x =
            match x with
            | "" :: xs -> metadataFallback xs
            | s :: _ -> s.Trim()
            | [] -> ""

        let rec metadataFallbackOpt x =
            match x with
            | "" :: xs -> metadataFallbackOpt xs
            | s :: _ -> Some (s.Trim())
            | [] -> None

        let findBackground() : string option =
            let guesses =
                [
                    sm.BACKGROUND
                    Path.ChangeExtension(sm.BACKGROUND, ".png")
                    Path.ChangeExtension(sm.BACKGROUND, ".jpg")
                    sm.TITLE + "-bg.jpg"
                    sm.TITLE + "-bg.png"
                    "bg.png"
                    "bg.jpg"
                    "background.png"
                    "background.jpg"
                ]
            match List.tryFind (fun guess -> File.Exists (Path.Combine(path, guess)) ) guesses with
            | Some p -> Some p
            | None ->

            let files = Directory.GetFiles path |> Array.map Path.GetFileName

            match files |> Array.tryFind (fun filename -> filename.ToLower().Contains "bg" || filename.ToLower().Contains "back") with
            | Some p -> Some p
            | None ->

            let image_files = 
                files
                |> Array.filter (fun f -> f.ToLower().Contains ".jpg" || f.ToLower().Contains ".png")
                |> Array.map (fun file -> 
                    try 
                        let info = Bitmap.Identify (Path.Combine(path, file))
                        file, info.Width * info.Height
                    with err -> file, 0
                )
            if image_files.Length >= 3 then //expect a bg, bn and cdtitle
                fst (Array.sortByDescending snd image_files).[0] |> Some
            else None

        let findAudio() : string option =
            if File.Exists (Path.Combine(path, sm.MUSIC)) then Some sm.MUSIC
            else

            let files = Directory.GetFiles path |> Array.map Path.GetFileName
                
            files |> Array.tryFind (fun filename -> filename.ToLower().Contains "mp3" || filename.ToLower().Contains "wav" || filename.ToLower().Contains "ogg")

        let findAuthor() : string =
            let folderName = Path.GetFileName path
            let paren = folderName.LastIndexOf('(')
            if paren > 1 then
                let guess = folderName.Substring (paren + 1)
                if guess.EndsWith(')') then guess.TrimEnd ')' else ""
            else
                let paren = folderName.LastIndexOf('[')
                let guess = folderName.Substring (paren + 1)
                if guess.EndsWith(']') then guess.TrimEnd ']' else ""
        
        let convert_difficulty (i: int) (diff: ChartData) : Chart = 
            let keys = diff.STEPSTYPE.Keycount
            let title = metadataFallback [sm.TITLETRANSLIT; sm.TITLE]
            let artist = metadataFallback [sm.ARTISTTRANSLIT; sm.ARTIST]
            let header = 
                {
                    Title = title
                    TitleNative = match metadataFallbackOpt [sm.TITLETRANSLIT] with Some t when t = title -> None | x -> x
                    Artist = artist
                    ArtistNative = match metadataFallbackOpt [sm.ARTISTTRANSLIT] with Some t when t = artist -> None | x -> x
                    Creator = metadataFallback [findAuthor(); sm.CREDIT; diff.CREDIT]
                    DiffName = sprintf "%O %O %O" diff.STEPSTYPE diff.DIFFICULTY diff.METER
                    Subtitle = metadataFallbackOpt [sm.SUBTITLETRANSLIT; sm.SUBTITLE]
                    Tags = sm.GENRE.Trim().Split(" ", StringSplitOptions.RemoveEmptyEntries) |> List.ofArray
                    Source = None

                    PreviewTime = sm.SAMPLESTART * 1000.0f<ms>
                    AudioFile = match findAudio() with Some file -> Relative file | None -> Missing
                    BackgroundFile = match findBackground() with Some file -> Relative file | None -> Missing

                    ChartSource = Unknown
                }
            if not (File.Exists (Path.Combine(path, match header.BackgroundFile with Relative r -> r | _ -> ""))) then
                Logging.Warn(sprintf "Background file for %s not found: %s" path sm.BACKGROUND)
                for f in Directory.EnumerateFileSystemEntries path do Logging.Debug f
            if not (File.Exists (Path.Combine(path, match header.AudioFile with Relative r -> r | _ -> ""))) then
                Logging.Warn(sprintf "Audio file for %s not found: %s" path sm.MUSIC)
                for f in Directory.EnumerateFileSystemEntries path do Logging.Debug f
            let filepath = Path.Combine (path, diff.STEPSTYPE.ToString() + " " + diff.METER.ToString() + " [" + (string i) + "].yav")
            let (notes, bpm) = convert_measures keys diff.NOTES sm.BPMS (-sm.OFFSET * 1000.0f<ms>)

            {
                Keys = keys
                Header = header
                Notes = notes
                BPM = bpm
                SV = [||]

                LoadedFromPath = filepath
            }

        sm.Charts |> List.mapi convert_difficulty

module Interlude =

    let private convertSnapsToHitobjects (snaps: TimeArray<NoteRow>) keys =
        let columnToX k = (float k + 0.5) * 512.0 / float keys |> round
               
        let rec ln_lookahead k (snaps: TimeItem<NoteRow> list) =
            match snaps with
            | { Time = offset; Data = nr } :: ss -> if nr.[k] = NoteType.HOLDTAIL then offset else ln_lookahead k ss
            | [] -> failwith "hold note has no end"

        let rec convert (snaps: TimeItem<NoteRow> list) = seq {
            match snaps with
            | { Time = offset; Data = nr } :: ss ->
                for k = 0 to keys - 1 do
                    if nr.[k] = NoteType.NORMAL then
                        yield HitCircle ((columnToX k, 240.0), offset, enum 0, (enum 0, enum 0, 0, 0, ""))
                    elif nr.[k] = NoteType.HOLDHEAD then
                        yield HoldNote ((columnToX k, 240.0), offset, ln_lookahead k ss, enum 0, (enum 0, enum 0, 0, 0, ""))
                yield! convert ss
            | [] -> ()
        }
        convert (snaps |> Array.toList)
        |> List.ofSeq

    let rec private bpmDurations (points: TimeItem<BPM> list) (endTime: Time) = 
        if List.isEmpty points then failwith "no bpm point"
        let { Time = offset; Data = { MsPerBeat = msPerBeat } } = List.head points
        let mutable current : float32<ms/beat> = msPerBeat
        let mutable t : Time = offset
        let data = new Dictionary<float32<ms/beat>, Time>()

        for { Time = offset; Data = { MsPerBeat = msPerBeat } } in points do
            if (not (data.ContainsKey current)) then data.Add(current, 0.0f<ms>)
            data.[current] <- data.[current] + offset - t; t <- offset; current <- msPerBeat
        if (not (data.ContainsKey current)) then data.Add(current, 0.0f<ms>)
        data.[current] <- data.[current] + endTime - t
        data

    let minMaxBPM bs endTime =
        let d = (bpmDurations bs endTime).OrderBy(fun p -> p.Key)
        (d.First().Key, d.Last().Key)

    let private convertToTimingPoints (bpm: TimeArray<BPM>) (sv: TimeArray<float32>) (endTime: Time) =

        let corrective_sv offset mult = 
            if sv.Length = 0 then None else
                let index = TimeArray.find_left offset sv
                if index < 0 then None
                else
                    let { Time = time; Data = value } = sv.[index]
                    if time = offset then None
                    else Some (TimingPoint.SV (offset, mult * value, (SampleSet.Soft, 0, 10), enum 0))

        let svs time1 time2 mult =
            seq {
                match corrective_sv time1 mult with
                | None -> ()
                | Some x -> yield x
                for { Time = offset; Data = value } in TimeArray.between time1 time2 sv do
                    yield TimingPoint.SV (offset, mult * value, (SampleSet.Soft, 0, 10), enum 0)
            }

        let tps =
            seq {
                let mutable bs = bpm |> List.ofArray
                if List.isEmpty bs then () else

                let mostCommonMspb = (bpmDurations bs endTime).OrderByDescending(fun p -> p.Value).First().Key

                yield! svs (-Time.infinity) (List.head bs).Time 1.0f
                while not (List.isEmpty bs) do
                    match bs with
                    | { Time = offset; Data = { Meter = meter; MsPerBeat = beatLength } } :: { Time = offset2 } :: rs ->
                        yield TimingPoint.BPM (offset, beatLength, meter, (SampleSet.Soft, 0, 10), enum 0)
                        yield! svs offset offset2 (mostCommonMspb / beatLength)
                        bs <- List.tail bs
                    | { Time = offset; Data = { Meter = meter; MsPerBeat = beatLength } } :: [] ->
                        yield TimingPoint.BPM (offset, beatLength, meter, (SampleSet.Soft, 0, 10), enum 0)
                        yield! svs offset Time.infinity (mostCommonMspb / beatLength)
                        bs <- List.tail bs
                    | [] -> failwith "impossible by loop condition"
            }
        tps |> List.ofSeq

    let toOsu (chart: Chart) : Beatmap =
        let general = 
            { General.Default with
                AudioFilename = match chart.Header.AudioFile with Relative s -> s | Absolute s -> Path.GetFileName s | Asset _ | Missing -> "audio.mp3"
                PreviewTime = chart.Header.PreviewTime
                SampleSet = SampleSet.Soft
                Mode = GameMode.Mania
            }
        let editor = Editor.Default
        let meta =
            { Metadata.Default with
                Title = chart.Header.Title
                TitleUnicode = Option.defaultValue chart.Header.Title chart.Header.TitleNative
                Artist = chart.Header.Artist
                ArtistUnicode = Option.defaultValue chart.Header.Artist chart.Header.ArtistNative
                Creator = chart.Header.Creator
                Version = chart.Header.DiffName
            }
        let diff =
            { Difficulty.Default with
                CircleSize = float chart.Keys
                OverallDifficulty = 8.0
                HPDrainRate = 8.0
            }
        {
            General = general
            Editor = editor
            Metadata = meta
            Difficulty = diff
            Events = [ Background ((match chart.Header.BackgroundFile with Relative s -> s | Absolute s -> Path.GetFileName s | Asset _ | Missing -> "bg.png"), (0.0, 0.0)) ]
            Objects = convertSnapsToHitobjects chart.Notes chart.Keys
            Timing = convertToTimingPoints chart.BPM chart.SV chart.LastNote
        }

    let toStepmania (chart: Chart) : StepmaniaData = failwith "nyi"

(*
    Overall utilities to dynamically load different chart files and convert to Interlude format
*)

[<AutoOpen>]
module Utilities =

    let (|ChartFile|_|) (path: string) =
        let s = Path.GetExtension(path).ToLower()
        match s with
        | ".sm" | ".osu" -> Some s
        | _ -> None

    let (|ChartArchive|_|) (path: string) = 
        match Path.GetExtension(path).ToLower() with
        | ".osz" | ".zip" -> Some ()
        | _ -> None

    let (|SongFolder|_|) (path: string) =
        if Directory.Exists path then
            Directory.EnumerateFiles path
            |> Seq.tryPick (fun x -> match x with ChartFile s -> Some s | _ -> None)
        else None
    
    let (|PackFolder|_|) (path: string) =
        if Directory.Exists path then
            Directory.EnumerateDirectories path
            |> Seq.forall (fun x -> match x with SongFolder _ -> false | _ -> true)
            |> fun b -> if b then None else Some ()
        else None

    let (|FolderOfPacks|_|) (path: string) =
        if Directory.Exists path then
            Directory.EnumerateDirectories path
            |> Seq.forall (fun x -> match x with PackFolder -> false | _ -> true)
            |> fun b -> if b then None else Some ()
        else None

    let loadAndConvertFile (action: ConversionAction) : Chart list =
        match Path.GetExtension(action.Source).ToLower() with
        | ".sm" -> 
            let f = 
                if action.Config.StepmaniaPackId.IsSome then
                    fun chart -> { chart with Header = { chart.Header with ChartSource = Stepmania action.Config.StepmaniaPackId.Value } }
                else id

            Stepmania.toInterlude (loadStepmaniaFile action.Source) action
            |> List.map f
        | ".osu" -> 
            try
                let map = loadBeatmapFile action.Source
                if 
                    map.General.Mode = GameMode.Mania
                    && (let keys = map.Difficulty.CircleSize |> int in 3 <= keys && keys <= 10)
                    && not (``osu!``.looks_like_a_rate map)
                then
                    [ ``osu!``.toInterlude (loadBeatmapFile action.Source) action ]
                else []
            with err -> Logging.Debug ("Skipped .osu file: " + action.Source, err); []
        | _ -> []