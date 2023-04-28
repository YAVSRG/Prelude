namespace Prelude.Data.Content

open System.IO
open System.IO.Compression
open Percyqaz.Common
open Percyqaz.Json
open Prelude.Common
open Prelude.Scoring

(*
    Default config values for themes, textures, noteskins, widget layouts
*)

[<Json.AutoCodec(false)>]
type ThemeConfig = 
    {
        Name: string
        ClearColors: Color * Color
        PBColors: Color array
        Font: string
        DefaultAccentColor: Color
        OverrideAccentColor: bool
        CursorSize: float32
    } 
    static member Default : ThemeConfig = 
        {
            Name = "Unnamed Theme"
            ClearColors = (Color.FromArgb(255, 127, 255, 180), Color.FromArgb(255, 255, 160, 140))
            PBColors = 
                [|
                    Color.Transparent
                    Color.FromArgb(160, 255, 160)
                    Color.FromArgb(160, 255, 255)
                    Color.FromArgb(255, 160, 80)
                |]
            Font = "Interlude"
            DefaultAccentColor = Color.FromArgb(0, 160, 200)
            OverrideAccentColor = false
            CursorSize = 50.0f
        }
    member this.Validate : ThemeConfig =
        { this with
            PBColors = 
                if this.PBColors.Length <> 4 then
                    Logging.Debug "Problem with theme: PBColors should have exactly 4 colors"
                    ThemeConfig.Default.PBColors
                else this.PBColors
        }

type Theme(storage) as this =
    inherit Storage(storage)

    let mutable config : ThemeConfig = ThemeConfig.Default
    do config <- 
        match this.TryGetJson<ThemeConfig> (true, "theme.json") with
        | Some data -> data.Validate
        | None -> failwith "theme.json was missing or didn't load properly"

    member this.Config
        with set conf = config <- conf; this.WriteJson (config, "theme.json")
        and get () = config
        
    member this.GetTexture (name: string) : (Bitmap * TextureConfig) option =
        let name = 
            if 
                (name = "logo" || name = "rain")
                && (let dayOfYear = System.DateTime.Today.DayOfYear in dayOfYear < 5 || dayOfYear > 350)
            then name + "-winterlude" else name

        match this.LoadTexture (name, "Textures") with
        | Ok res -> res
        | Error err -> Logging.Error(sprintf "Error loading theme texture '%s': %s" name err.Message); None

    member this.GetSound (name: string) : Stream option =
        this.TryReadFile("Sounds", name + ".wav")
            
    member this.GetRulesetTexture (name: string) : (Bitmap * TextureConfig) =
        match this.LoadTexture (name, "Rulesets") with
        | Ok (Some res) -> res
        | _ -> new Bitmap(1, 1), TextureConfig.Default // swallow error or missing file

    member this.GetRulesets() =
        seq {
            for config in this.GetFiles "Rulesets" do
                if Path.GetExtension(config).ToLower() = ".ruleset" then
                    match this.TryGetJson<Ruleset>(true, "Rulesets", config) with
                    | Some data -> yield Path.GetFileNameWithoutExtension config, data.Validate
                    | None -> () // Error has already been logged when this happens
        }

    member this.GetFonts() =
        seq {
            for file in this.GetFiles "Fonts" do
                match Path.GetExtension(file).ToLower() with
                | ".otf" | ".ttf" ->
                    match this.TryReadFile("Fonts", file) with
                    | Some s -> 
                        // Font loading requires seek
                        use ms = new MemoryStream()
                        s.CopyTo ms
                        ms.Position <- 0
                        yield ms; s.Dispose()
                    | None -> ()
                | _ -> ()
        }
        
    static member FromZipFile (file: string) = 
        let stream = File.OpenRead file
        new Theme(Zip (new ZipArchive(stream), Some file))
    static member FromZipStream (stream: Stream) = new Theme(Zip (new ZipArchive(stream), None))
    static member FromPath (path: string) = new Theme(Folder path)
    static member FromFolderName (name: string) = Theme.FromPath(getDataPath (Path.Combine ("Themes", name)))