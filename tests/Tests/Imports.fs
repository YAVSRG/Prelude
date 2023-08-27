﻿namespace Prelude.Test

open System.IO
open Percyqaz.Common
open Prelude.Charts.Formats.Interlude
open Prelude.Charts.Formats.Conversions
open Prelude.Data.Charts

module Imports =

    let main() =

        Logging.Info "Running converter on all osu! songs ..."
        for song in Directory.EnumerateDirectories Library.Imports.osuSongFolder do
            for file in Directory.EnumerateFiles song do
                if file.ToLower().EndsWith(".osu") then
                    for chart in loadAndConvertFile { Source = file; Config = ConversionOptions.Default } do
                        match Chart.check chart with Error msg -> Logging.Error msg | Ok() -> ()
        Logging.Info "Complete!"