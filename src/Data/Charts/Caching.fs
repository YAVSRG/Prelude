﻿namespace Prelude.Data.Charts

open System
open System.Collections.Generic
open Percyqaz.Json
open Percyqaz.Common
open Prelude.Common
open Prelude.Charts.Formats.Interlude
open Prelude.Charts.Formats.Conversions
open Prelude.Scoring
open Prelude.Gameplay.Mods
open Prelude.Gameplay.Layout
open Prelude.Gameplay.Difficulty

module Caching =

    [<Json.AutoCodec>]
    type CachedChart =
        {
            FilePath: string
            Title: string
            Artist: string
            Creator: string
            Pack: string
            Hash: string
            Keys: int
            Length: Time
            BPM: (float32<ms/beat> * float32<ms/beat>)
            DiffName: string
            Physical: float
            Technical: float
        }

    let cacheChart (chart: Chart) : CachedChart =
        let lastNote = chart.LastNote
        let rating = RatingReport(chart.Notes, 1.0f, Layout.Spread, chart.Keys)
        {
            FilePath = chart.FileIdentifier
            Title = chart.Header.Title
            Artist = chart.Header.Artist
            Creator = chart.Header.Creator
            Pack = chart.Header.SourcePack
            Hash = Chart.hash chart
            Keys = chart.Keys
            Length = lastNote - chart.FirstNote
            // todo: move to Chart module
            BPM = ``Interlude to osu!``.minMaxBPM (List.ofSeq chart.BPM.Data) lastNote
            DiffName = chart.Header.DiffName
            Physical = rating.Physical
            Technical = rating.Technical
        }