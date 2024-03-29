﻿namespace Prelude.Charts.Tools

open System
open Percyqaz.Common
open Percyqaz.Json
open Prelude
open Prelude.Charts

// This is the final stage of preprocessing chart data before it is played by the user.
// Colorings are an assignment of a color id for each note. These ids are then used by skins to display differences in textures
// Some players may find certain coloring systems useful, for example having color coding depending on the musical beat a note is snapped to
// It is also common just to have simple column color variation to make columns appear distinct

module NoteColors =

    let DDR_VALUES =
        [| 1.0f; 2.0f; 3.0f; 4.0f; 6.0f; 8.0f; 12.0f; 16.0f |]
        |> Array.map (fun i -> i * 1.0f< / beat>)

    type ColorScheme =
        | Column = 0
        | Chord = 1
        | DDR = 2

    module ColorScheme =
        let count (keycount: int) (scheme: ColorScheme) =
            match scheme with
            | ColorScheme.Column -> keycount
            | ColorScheme.Chord -> keycount
            | ColorScheme.DDR -> Array.length DDR_VALUES + 1
            | _ -> keycount

    type ColorData = byte array
    type ColorDataSets = ColorData array // color config per keymode. 0 stores "all keymode" data, 1 stores 3k, 2 stores 4k, etc
    type Colorizer<'state> = 'state -> TimeItem<NoteRow> -> ('state * ColorData)

    type private ColorNoteRow = (struct (NoteRow * ColorData))

    type ColorizedChart =
        {
            Keys: int
            Notes: TimeArray<ColorNoteRow>
            BPM: TimeArray<BPM>
            SV: TimeArray<float32>
            ModsUsed: string list
        }
        member this.FirstNote = this.Notes.[0].Time
        member this.LastNote = this.Notes.[this.Notes.Length - 1].Time

    let private roughly_divisible (a: Time) (b: Time) =
        Time.abs (a - b * float32 (Math.Round(float <| a / b))) < 3.0f<ms>

    let private ddr_func (delta: Time) (msPerBeat: float32<ms / beat>) : int =
        List.tryFind ((fun i -> DDR_VALUES.[i]) >> fun n -> roughly_divisible delta (msPerBeat / n)) [ 0..7 ]
        |> Option.defaultValue DDR_VALUES.Length

    let private column_colors (colorData: ColorData) (mc: ModChart) : TimeArray<ColorNoteRow> =

        let c = [| for i in 0 .. (mc.Keys - 1) -> colorData.[i] |]
        mc.Notes |> TimeArray.map (fun nr -> struct (nr, c))

    let private chord_colors (color_data: ColorData) (mc: ModChart) : TimeArray<ColorNoteRow> =

        let mutable previous_colors: ColorData = Array.zeroCreate mc.Keys

        mc.Notes
        |> TimeArray.map (fun nr ->

            let mutable index = -1

            for k = 0 to mc.Keys - 1 do
                if nr.[k] = NoteType.NORMAL || nr.[k] = NoteType.HOLDHEAD then
                    index <- index + 1

            index <- max 0 index

            let colors = Array.create mc.Keys color_data.[index]

            for k = 0 to mc.Keys - 1 do
                if nr.[k] = NoteType.HOLDBODY || nr.[k] = NoteType.HOLDTAIL then
                    colors.[k] <- previous_colors.[k]
                else
                    previous_colors.[k] <- color_data.[index]

            struct (nr, colors)
        )

    let private ddr_colors (color_data: ColorData) (mc: ModChart) : TimeArray<ColorNoteRow> =

        let mutable previous_colors: ColorData = Array.zeroCreate mc.Keys

        let mutable bpm_index = 0
        let mutable bpm_time = if mc.BPM.Length = 0 then 0.0f<ms> else mc.BPM.[0].Time

        let mutable bpm_mspb =
            if mc.BPM.Length = 0 then
                500.0f<ms / beat>
            else
                mc.BPM.[0].Data.MsPerBeat

        mc.Notes
        |> Array.map (fun { Time = time; Data = nr } ->

            while bpm_index < mc.BPM.Length - 1 && mc.BPM.[bpm_index + 1].Time < time do
                bpm_index <- bpm_index + 1
                bpm_time <- mc.BPM.[bpm_index].Time
                bpm_mspb <- mc.BPM.[bpm_index].Data.MsPerBeat

            let ddr_color = ddr_func (time - bpm_time) bpm_mspb

            let colors = Array.create mc.Keys color_data.[ddr_color]

            for k = 0 to mc.Keys - 1 do
                if nr.[k] = NoteType.HOLDBODY || nr.[k] = NoteType.HOLDTAIL then
                    colors.[k] <- previous_colors.[k]
                else
                    previous_colors.[k] <- color_data.[ddr_color]

            {
                Time = time
                Data = struct (nr, colors)
            }
        )

    let private apply_scheme (scheme: ColorScheme) (color_data: ColorData) (mc: ModChart) =
        let colored_notes =
            match scheme with
            | ColorScheme.Column -> column_colors color_data mc
            | ColorScheme.Chord -> chord_colors color_data mc
            | ColorScheme.DDR -> ddr_colors color_data mc
            | _ -> column_colors (Array.zeroCreate mc.Keys) mc

        {
            Keys = mc.Keys
            Notes = colored_notes
            BPM = mc.BPM
            SV = mc.SV
            ModsUsed = mc.ModsUsed
        }

    [<Json.AutoCodec(false)>]
    type ColorConfig =
        {
            Style: ColorScheme
            Colors: ColorDataSets
            UseGlobalColors: bool
        }
        static member Default =
            {
                Style = ColorScheme.Column
                Colors = Array.init 9 (fun i -> Array.init 10 byte)
                UseGlobalColors = true
            }

        member this.Validate =
            { this with
                Colors =
                    if
                        Array.forall (fun (x: ColorData) -> x.Length = 10) this.Colors
                        && this.Colors.Length = 9
                    then
                        this.Colors
                    else
                        Logging.Error(
                            "Problem with noteskin: Colors should be an 9x10 array - Please use the ingame editor"
                        )

                        ColorConfig.Default.Colors
            }

    let apply_coloring (config: ColorConfig) (chart: ModChart) : ColorizedChart =
        let index = if config.UseGlobalColors then 0 else chart.Keys - 2
        apply_scheme config.Style config.Colors.[index] chart
