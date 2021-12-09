namespace Prelude.Data

open System
open System.IO
open System.IO.Compression
open System.Drawing
open Prelude.Common
open Prelude.Scoring
open Prelude.Gameplay.NoteColors

module Themes =

    (*
        Default config values for themes, textures, noteskins, widget layouts
    *)

    type ThemeConfig = 
        {
            Name: string
            JudgementColors: Color array
            JudgementNames: string array
            LampColors: Color array
            LampNames: string array
            Grades: Grade array
            ClearColors: Color * Color
            PBColors: Color array
            Font: string
            DefaultAccentColor: Color
            OverrideAccentColor: bool
            CursorSize: float32
        } 
        static member Default : ThemeConfig = 
            {
                Name = "?"
                JudgementColors =
                    [|
                        Color.FromArgb(127, 127, 255)
                        Color.FromArgb(0, 255, 255)
                        Color.FromArgb(255, 255, 0)
                        Color.FromArgb(0, 255, 100)
                        Color.FromArgb(0, 0, 255)
                        Color.Fuchsia
                        Color.FromArgb(255, 0, 0)
                    |]
                JudgementNames =
                    [|
                        "Ridiculous"
                        "Marvellous"
                        "Perfect"
                        "Great"
                        "Good"
                        "Bad"
                        "Miss"
                    |]
                LampColors =
                    [|
                        Color.White
                        Color.FromArgb(255, 160, 160)
                        Color.FromArgb(160, 160, 160)
                        Color.FromArgb(80, 255, 80)
                        Color.FromArgb(160, 255, 160)
                        Color.FromArgb(200, 160, 255)
                        Color.FromArgb(255, 255, 80)
                        Color.FromArgb(255, 255, 160)
                        Color.FromArgb(255, 160, 255)
                        Color.FromArgb(160, 255, 255)
                    |]
                LampNames =
                    [|
                        "NONE"
                        "SINGLE DIGIT COMBO BREAKS"
                        "MISS FLAG"
                        "FULL COMBO"
                        "SINGLE DIGIT GREATS"
                        "BLACK FLAG"
                        "PERFECT FULL COMBO"
                        "SINGLE DIGIT PERFECTS"
                        "WHITE FLAG"
                        "MARVELLOUS FULL COMBO"
                    |]
                Grades =
                    [|
                        { Name = "F"; Accuracy = None; ComboBreaks = 1.0; Color = Color.FromArgb(200, 163, 155) }
                        { Name = "D"; Accuracy = Some 0.89995; ComboBreaks = 1.0; Color = Color.FromArgb(194, 162, 182) }
                        { Name = "C"; Accuracy = Some 0.90995; ComboBreaks = 1.0; Color = Color.FromArgb(202, 153, 183) }
                        { Name = "C+"; Accuracy = Some 0.91995; ComboBreaks = 1.0; Color = Color.FromArgb(163, 190, 207) }
                        { Name = "B"; Accuracy = Some 0.92995; ComboBreaks = 1.0; Color = Color.FromArgb(149, 193, 220) }
                        { Name = "B+"; Accuracy = Some 0.93995; ComboBreaks = 1.0; Color = Color.FromArgb(148, 210, 180) }
                        { Name = "A"; Accuracy = Some 0.94995; ComboBreaks = 1.0; Color = Color.FromArgb(134, 227, 183) }
                        { Name = "A+"; Accuracy = Some 0.95995; ComboBreaks = 0.016; Color = Color.FromArgb(127, 231, 139) }
                        { Name = "S-"; Accuracy = Some 0.96995; ComboBreaks = 0.08; Color = Color.FromArgb(237, 205, 140) }
                        { Name = "S"; Accuracy = Some 0.97995; ComboBreaks = 0.005; Color = Color.FromArgb(246, 234, 128) }
                        { Name = "S+"; Accuracy = Some 0.98995; ComboBreaks = 0.002; Color = Color.FromArgb(235, 200, 220) }
                    |]
                ClearColors = (Color.FromArgb(255, 127, 255, 180), Color.FromArgb(255, 255, 160, 140))
                PBColors = 
                    [|
                        Color.Transparent
                        Color.FromArgb(160, 255, 160)
                        Color.FromArgb(160, 255, 255)
                        Color.FromArgb(255, 160, 80)
                    |]

                Font = "Akrobat-Black.otf"
                DefaultAccentColor = Color.FromArgb(0, 160, 200)
                OverrideAccentColor = false
                CursorSize = 50.0f
            }

    type TextureConfig =
        {
            Columns: int
            Rows: int
            Tiling: bool
        }   
        static member Default =
            {
                Columns = 1
                Rows = 1
                Tiling = true
            }

    type WidgetConfig =
        {
            Enabled: bool
            Float: bool
            Left: float32
            LeftA: float32
            Top: float32
            TopA: float32
            Right: float32
            RightA: float32
            Bottom: float32
            BottomA: float32
        }
        static member Default =
            {
                Enabled = false
                Float = true
                Left = 0.0f
                LeftA = 0.0f
                Top = 0.0f
                TopA = 0.0f
                Right = 0.0f
                RightA = 1.0f
                Bottom = 0.0f
                BottomA = 1.0f
            }

    module WidgetConfig =
        type AccuracyMeter = 
            { 
                Position: WidgetConfig
                GradeColors: bool
                ShowName: bool
            }
            static member Default = 
                {
                    Position =
                        { 
                            Enabled = true
                            Float = false
                            Left = -100.0f
                            LeftA = 0.5f
                            Top = 40.0f
                            TopA = 0.0f
                            Right = 100.0f
                            RightA = 0.5f
                            Bottom = 120.0f
                            BottomA = 0.0f
                        }
                    GradeColors = true
                    ShowName = true
                }

        type HitMeter =
            {
                Position: WidgetConfig
                AnimationTime: float32
                Thickness: float32
                ShowGuide: bool
            }
            static member Default = 
                {
                    Position =
                        { 
                            Enabled = true
                            Float = false
                            Left = -300.0f
                            LeftA = 0.5f
                            Top = 0.0f
                            TopA = 0.5f
                            Right = 300.0f
                            RightA = 0.5f
                            Bottom = 25.0f
                            BottomA = 0.5f
                        }
                    AnimationTime = 1000.0f
                    Thickness = 5.0f
                    ShowGuide = true
                }

        type LifeMeter =
            {
                Position: WidgetConfig
                Horizontal: bool
                EndColor: Color
                FullColor: Color
                EmptyColor: Color
            }
            static member Default =
                {
                    Position =
                        {
                            Enabled = true
                            Float = false
                            Left = 0.0f
                            LeftA = 1.0f
                            Top = 0.0f
                            TopA = 0.6f
                            Right = 20.0f
                            RightA = 1.0f
                            Bottom = 0.0f
                            BottomA = 1.0f
                        }
                    Horizontal = false
                    EndColor = Color.FromArgb(100, Color.Red)
                    FullColor = Color.White
                    EmptyColor = Color.Red
                }

        type Combo =
            {
                Position: WidgetConfig
                Growth: float32
                Pop: float32
                LampColors: bool
            }
            static member Default = 
                {
                    Position =
                        { 
                            Enabled = true
                            Float = false
                            Left = -100.0f
                            LeftA = 0.5f
                            Top = -10.0f
                            TopA = 0.45f
                            Right = 100.0f
                            RightA = 0.5f
                            Bottom = 50.0f
                            BottomA = 0.45f
                        }
                    Growth = 0.01f
                    Pop = 5.0f
                    LampColors = true
                }

        type SkipButton =
            { Position: WidgetConfig }
            static member Default =
                {
                    Position =
                        {
                            Enabled = true
                            Float = true
                            Left = -200.0f
                            LeftA = 0.5f
                            Top = 20.0f
                            TopA = 0.6f
                            Right = 200.0f
                            RightA = 0.5f
                            Bottom = 120.0f
                            BottomA = 0.6f
                        }
                }

        type JudgementMeter =
            {
                Position: WidgetConfig
                AnimationTime: float32
                ShowRDMA: bool
            }
            static member Default = 
                {
                    Position = 
                        {
                            Enabled = true
                            Float = false
                            Left = -128.0f
                            LeftA = 0.5f
                            Top = 30.0f
                            TopA = 0.5f
                            Right = 128.0f
                            RightA = 0.5f
                            Bottom = 86.0f
                            BottomA = 0.5f
                        }
                    AnimationTime = 800.0f
                    ShowRDMA = true
                }
        
        type Explosions =
            {
                FadeTime: float32
                ExpandAmount: float32
                ExplodeOnMiss: bool
                AnimationFrameTime: float
            }
            static member Default =
                {
                    FadeTime = 1.0f
                    ExpandAmount = 0.15f
                    ExplodeOnMiss = false
                    AnimationFrameTime = 50.0
                }

        type ProgressBar = { Position: WidgetConfig }
        //song info
        //mod info
        //current real time
        //current song time
        //life meter
        //judgement counts
        //pacemaker
        
    // Texture names that are loaded from Noteskin folders instead of Theme folders
    let noteskinTextures = [|"note"; "noteexplosion"; "receptor"; "holdhead"; "holdbody"; "holdtail"; "holdexplosion"; "judgements"|]
        
    type NoteSkinConfig =
        {
            Name: string
            Author: string
            Version: string

            /// Enables rotation for notes. Set this to true if your notes are arrows/should rotate depending on which column they are in
            UseRotation: bool
            /// Contains settings for the color scheme of notes
            NoteColors: ColorConfig

            /// Hold tail textures are oriented for upscroll. Set this to true if you want them to be flipped when not playing in downscroll mode.
            FlipHoldTail: bool
            /// Set to false if you want to use the `holdhead` texture for hold tails too
            UseHoldTailTexture: bool
            /// Visually shortens hold notes by the given number of pixels
            HoldNoteTrim: float32
            /// Sets the color that hold notes should turn when they are not being held
            DroppedHoldColor: Color

            /// Sets the color of the playfield behind notes
            PlayfieldColor: Color
            /// Sets the alignment of the playfield - 0.5, 0.5 lines up the middle of the playfield with the middle of the screen
            PlayfieldAlignment: float32 * float32
            /// Sets the width of columns, in pixels
            ColumnWidth: float32
            // todo: group every animation thing under an animations object
            /// ???
            ColumnLightTime: float32

            /// ???
            AnimationFrameTime: float
            /// Config for explosion animations
            Explosions: WidgetConfig.Explosions
        }
        static member Default =
            {
                Name = "Unnamed Noteskin"
                Author = "Unknown"
                Version = "1.0.0"
                UseRotation = false
                FlipHoldTail = true
                UseHoldTailTexture = true
                HoldNoteTrim = 0.0f
                PlayfieldColor = Color.FromArgb(120, 0, 0, 0)
                PlayfieldAlignment = 0.5f, 0.5f
                DroppedHoldColor = Color.FromArgb(180, 180, 180, 180)
                ColumnWidth = 150.0f
                ColumnLightTime = 0.4f
                AnimationFrameTime = 200.0
                Explosions = WidgetConfig.Explosions.Default
                NoteColors = ColorConfig.Default
            }

    (*
        Basic theme I/O stuff. Additional implementation in Interlude for texture-specific things that depend on Interlude
    *)

    type StorageType = Zip of ZipArchive * source: string option | Folder of string

    type StorageAccess(storage: StorageType) =

        member this.StorageType = storage

        member this.TryReadFile ([<ParamArray>] path: string array) =
            let p = Path.Combine(path)
            try
                match storage with
                | Zip (z, _) -> z.GetEntry(p.Replace(Path.DirectorySeparatorChar, '/')).Open() |> Some
                | Folder f ->
                    let p = Path.Combine(f, p)
                    File.OpenRead p :> Stream |> Some
            with
            | :? FileNotFoundException | :? DirectoryNotFoundException // File doesnt exist in folder storage
            | :? NullReferenceException -> None // File doesnt exist in zip storage
            | _ -> reraise()
        
        member this.GetFiles ([<ParamArray>] path: string array) =
            let p = Path.Combine(path)
            match storage with
            | Zip (z, _) ->
                let p = p.Replace(Path.DirectorySeparatorChar, '/')
                seq {
                    for e in z.Entries do
                        if e.FullName = p + "/" + e.Name && Path.HasExtension e.Name then yield e.Name
                }
            | Folder f ->
                let target = Path.Combine(f, p)
                Directory.CreateDirectory target |> ignore
                Directory.EnumerateFiles target |> Seq.map Path.GetFileName

        member this.GetFolders ([<ParamArray>] path: string array) =
            let p = Path.Combine path
            match storage with
            | Zip (z, _) ->
                let p = p.Replace (Path.DirectorySeparatorChar, '/')
                seq {
                    for e in z.Entries do
                        if e.Name = "" && e.FullName.Length > p.Length then
                            let s = e.FullName.Substring(p.Length + 1).Split('/')
                            if e.FullName = p + "/" + s.[0] + "/" then yield s.[0]
                }
            | Folder f ->
                let target = Path.Combine(f, p)
                Directory.CreateDirectory target |> ignore
                Directory.EnumerateDirectories target |> Seq.map Path.GetFileName

        member this.GetJson<'T> (createNew: bool, [<ParamArray>] path: string array) : 'T * bool =
            let defaultValue() = JSON.Default<'T>(), match storage with Folder f -> false | _ -> true
            try
                let mutable rewrite = createNew
                let json, success =
                    match this.TryReadFile path with
                    | Some stream ->
                        use tr = new StreamReader(stream)
                        let json, success =
                            tr.ReadToEnd()
                            |> JSON.FromString<'T>
                            |> function Ok v -> (v, true) | _ -> defaultValue()
                        stream.Dispose()
                        rewrite <- true
                        json, success
                    | None -> defaultValue()
                if createNew then this.WriteJson (json, path)
                json, success
            with err ->
                Logging.Error (
                    sprintf "Couldn't load json '%s' in '%s'"
                        (String.concat "/" path)
                        (
                            match storage with
                            | Zip (_, source) -> match source with Some s -> Path.GetFileName s | None -> "EMBEDDED ASSETS"
                            | Folder f -> Path.GetFileName f
                        ),
                    err
                )
                defaultValue()

        member this.WriteJson<'T> (data: 'T, [<ParamArray>] path: string array) =
            match storage with
            | Zip _ -> () // Cannot write data to zip archives
            | Folder f ->
                let target = Path.Combine(f, Path.Combine path)
                target |> Path.GetDirectoryName |> Directory.CreateDirectory |> ignore
                JSON.ToFile(target, true) data

        member this.CopyTo targetPath =
            Directory.CreateDirectory targetPath |> ignore
            match storage with
            | Zip (z, _) -> z.ExtractToDirectory targetPath
            | Folder f -> failwith "NYI, do this manually for now"


    type Theme(storage) as this =
        inherit StorageAccess(storage)

        let mutable config : ThemeConfig = ThemeConfig.Default
        do config <- this.GetJson (false, "theme.json") |> fst

        member this.Config
            with set conf = config <- conf; this.WriteJson (config, "theme.json")
            and get () = config
        
        member this.GetTexture (name: string) =
            match this.TryReadFile ("Textures", name + ".png") with
            | Some stream ->
                let bmp = new Bitmap(stream)
                let info : TextureConfig = this.GetJson<TextureConfig> (false, "Textures", name + ".json") |> fst
                stream.Dispose()
                Some (bmp, info)
            | None -> None

        member this.GetGameplayConfig<'T> (name: string) = this.GetJson<'T> (true, "Interface", "Gameplay", name + ".json")
        
        static member FromZipFile (file: string) = 
            let stream = File.OpenRead file
            new Theme(Zip (new ZipArchive(stream), Some file))
        static member FromZipStream (stream: Stream) = new Theme(Zip (new ZipArchive(stream), None))
        static member FromFolderName (name: string) = new Theme(Folder <| getDataPath (Path.Combine ("Themes", name)))

    type NoteSkin(storage) as this =
        inherit StorageAccess(storage)
        
        let mutable config : NoteSkinConfig = NoteSkinConfig.Default
        do config <- this.GetJson (false, "noteskin.json") |> fst
        
        member this.Config
            with set conf = config <- conf; this.WriteJson (config, "noteskin.json")
            and get () = config
            
        member this.GetTexture (name: string) =
            match this.TryReadFile (name + ".png") with
            | Some stream ->
                let bmp = new Bitmap(stream)
                let info : TextureConfig = this.GetJson<TextureConfig> (false, name + ".json") |> fst
                stream.Dispose()
                Some (bmp, info)
            | None -> None
        
        static member FromZipFile (file: string) = 
            let stream = File.OpenRead file
            new NoteSkin(Zip (new ZipArchive(stream), Some file))
        static member FromZipStream (stream: Stream) = new NoteSkin(Zip (new ZipArchive(stream), None))
        static member FromFolder (path: string) = new NoteSkin(Folder path)