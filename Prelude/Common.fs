﻿namespace Prelude

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open System.Collections.Generic
open System.Text.RegularExpressions
open Percyqaz.Json

module Common =

    [<Measure>] type ms
    [<Measure>] type beat
    [<Measure>] type minute
    type Time = float32<ms>
    let inline toTime (f: float) = float32 f * 1.0f<ms>

    module Time =
        let Abs(t: Time) = if t < 0.0f<ms> then -t else t

(*
    Settings - Store an (ideally immutable type) value that can be get and set
    Aims to provide extension to add restrictions and a consistent format so that it is easy to auto-generate UI components that edit settings
    (Auto generation will be done with reflection)
*)

    [<AbstractClass>]
    type ISettable<'T>() =
        abstract member Set: 'T -> unit
        abstract member Get: unit -> 'T

    type Setting<'T>(value : 'T) =
        inherit ISettable<'T>()
        let mutable value = value
        override this.Set(newValue) = value <- newValue
        override this.Get() = value
        override this.ToString() = value.ToString()
        static member Pickler: Json.Mapping.JsonPickler<Setting<'T>> =
            let tP = Json.Mapping.getPickler<'T>()
            Json.Mapping.mkPickler
                (fun (o: Setting<'T>) -> tP.Encode(o.Get()))
                (fun (o: Setting<'T>) json -> tP.Decode(o.Get())(json) |> JsonMapResult.map (fun v -> o.Set(v); o))

    type WrappedSetting<'T, 'U>(setting: ISettable<'U>, set: 'T -> 'U, get: 'U -> 'T) =
        inherit ISettable<'T>()
        override this.Set(newValue) = setting.Set(set(newValue))
        override this.Get() = get(setting.Get())

    [<AbstractClass>]
    type NumSetting<'T when 'T : comparison>(value : 'T, min : 'T, max : 'T) =
        inherit Setting<'T>(value)
        abstract member SetPercent: float32 -> unit
        abstract member GetPercent: unit -> float32
        override this.Set(newValue) = base.Set(if newValue > max then max elif newValue < min then min else newValue)
        member this.Min = min
        member this.Max = max
        override this.ToString() = sprintf "%s (%A - %A)" (base.ToString()) min max
        static member Pickler: Json.Mapping.JsonPickler<NumSetting<'T>> =
            let tP = Json.Mapping.getPickler<'T>()
            Json.Mapping.mkPickler
                (fun (o: NumSetting<'T>) -> tP.Encode(o.Get()))
                (fun (o: NumSetting<'T>) json -> tP.Decode(o.Get())(json) |> JsonMapResult.map (fun v -> o.Set(v); o))

    type IntSetting(value: int, min: int, max: int) = 
        inherit NumSetting<int>(value, min, max)
        override this.SetPercent(pc: float32) = this.Set(min + ((float32 (max - min) * pc) |> float |> Math.Round |> int))
        override this.GetPercent() = float32 (this.Get() - min) / float32 (max - min)
        static member Pickler = Setting<int>.Pickler

    type FloatSetting(value: float, min: float, max: float) = 
        inherit NumSetting<float>(value, min, max)
        override this.SetPercent(pc: float32) = this.Set(min + (max - min) * float pc)
        override this.GetPercent() = (this.Get() - min) / (max - min) |> float32
        override this.Set(newValue: float) = base.Set(Math.Round(newValue, 2))
        static member Pickler = Setting<float>.Pickler

    type StringSetting(value: string, allowSpecialChar: bool) =
        inherit Setting<string>(value)
        override this.Set(newValue) = base.Set(if allowSpecialChar then newValue else Regex("[^a-zA-Z0-9_-]").Replace(newValue, ""))
        static member Pickler = Setting<string>.Pickler

(*
    Logging
*)

    type LoggingLevel = DEBUG = 0 | INFO = 1 | WARNING = 2 | ERROR = 3 | CRITICAL = 4
    type LoggingEvent = LoggingLevel * string * string

    type Logging() =
        static let evt = new Event<LoggingEvent>()
        
        static let agent = new MailboxProcessor<LoggingEvent>(fun box -> async { while (true) do let! e = box.Receive() in evt.Trigger(e) })
        static do agent.Start()

        static member Subscribe f = evt.Publish.Add f
        static member Log level main details = agent.Post((level, main, details))
        static member Info = Logging.Log LoggingLevel.INFO
        static member Warn = Logging.Log LoggingLevel.WARNING
        static member Error = Logging.Log LoggingLevel.ERROR
        static member Debug = Logging.Log LoggingLevel.DEBUG
        static member Critical = Logging.Log LoggingLevel.CRITICAL

    Logging.Subscribe (fun (level, main, details) -> printfn "[%A]: %s" level main; if level = LoggingLevel.CRITICAL then printfn " .. %s" details)

(*
    Localisation
*)

    module Localisation =
        let private mapping = new Dictionary<string, string>()
        let mutable private loadedPath = ""

        let loadFile path =
            let path = Path.Combine("Locale", path)
            try
                let lines = File.ReadAllLines(path)
                Array.iter(
                    fun (l: string) ->
                        let s: string[] = l.Split([|'='|], 2)
                        mapping.Add(s.[0], s.[1])) lines
                loadedPath <- path
            with
            | err -> Logging.Error("Failed to load localisation file: " + path)(err.ToString())

        let localise str : string =
            if mapping.ContainsKey(str) then mapping.[str]
            else 
                mapping.Add(str, str)
                if loadedPath <> "" then File.AppendAllText(loadedPath, "\n"+str+"="+str)
                str

        let localiseWith xs str =
            let mutable s = localise str
            List.iteri (fun i x -> s <- s.Replace("%"+i.ToString(), x)) xs
            s

(*
    Background task management
*)

    type StatusTask = (string -> unit) -> Async<bool>
    type TaskFlags = NONE = 0 | HIDDEN = 1 | LONGRUNNING = 2

    module BackgroundTask =
        //Some race conditions exist but the data is only used by GUI code (and so only needs informal correctness)
        type ManagedTask (name: string, t: StatusTask, options: TaskFlags, removeTask) as this =
            let cts = new CancellationTokenSource()
            let mutable info = ""
            let mutable complete = false
            let visible = options &&& TaskFlags.HIDDEN <> TaskFlags.HIDDEN

            let task =
                Async.StartAsTask(
                    async {
                        try
                            Logging.Debug(sprintf "Task <%s> started" name) ""
                            info <- "Running"
                            let! outcome = t (fun s -> info <- s; if not visible then Logging.Debug (sprintf "[%s] %s" name s) "")
                            Logging.Debug(sprintf "Task <%s> complete (%A)" name outcome) ""
                            info <- "Complete"
                        with
                        | err ->
                            Logging.Error(sprintf "Exception in task '%s'" name) (err.ToString())
                            info <- sprintf "Failed: %O" <| err.GetType()
                        removeTask(this)
                        complete <- true
                        cts.Dispose()
                        },
                    ((*if options &&& TaskFlags.LONGRUNNING = TaskFlags.LONGRUNNING then TaskCreationOptions.LongRunning else*) TaskCreationOptions.None),
                    cts.Token)

            member this.Name = name
            member this.Status = task.Status
            member this.Cancel() =
                if complete then Logging.Warn(sprintf "Task <%s> already finished, can't cancel!" name) ""
                else Logging.Debug(sprintf "Task <%s> cancelled" name) ""; cts.Cancel(false)
            member this.Visible = visible
            member this.Info = info

        let private evt = new Event<ManagedTask>()
        let Subscribe f = evt.Publish.Add f

        let TaskList = ResizeArray<ManagedTask>()
        let private removeTask = fun mt -> if lock(TaskList) (fun() -> TaskList.Remove(mt)) <> true then Logging.Debug(sprintf "Tried to remove a task that isn't there: %s" mt.Name) ""

        let Callback (f: bool -> unit) (proc: StatusTask): StatusTask = fun (l: string -> unit) -> async { let! v = proc(l) in f(v); return v }
        let rec Chain (procs: StatusTask list): StatusTask =
            match procs with
            | [] -> failwith "should not be used on empty list"
            | x :: [] -> x
            | x :: xs ->
                let rest = Chain(xs)
                fun (l: (string -> unit)) ->
                    async { let! b = x(l) in if b then return! rest(l) else return false }

        let Create (options: TaskFlags) (name: string) (t: StatusTask) = let mt = new ManagedTask(name, t, options, removeTask) in lock(TaskList)(fun() -> TaskList.Add(mt)); evt.Trigger(mt); mt

(*
    Random helper functions (mostly data storage)
*)

    let getDataPath name =
        let p = Path.Combine(Directory.GetCurrentDirectory(), name)
        Directory.CreateDirectory(p) |> ignore
        p

    let loadImportantJsonFile<'T> name path (defaultData: 'T) prompt =
        if File.Exists(path) then
            let p = Path.ChangeExtension(path, ".bak")
            if File.Exists(p) then File.Copy(p, Path.ChangeExtension(path, ".bak2"), true)
            File.Copy(path, p, true)
            try
                Json.fromFile(path) |> JsonResult.value
            with err ->
                Logging.Critical(sprintf "Could not load %s! Maybe it is corrupt?" <| Path.GetFileName(path)) (err.ToString())
                if prompt then
                    Console.WriteLine("If you would like to launch anyway, press ENTER.")
                    Console.WriteLine("If you would like to try and fix the problem youself, CLOSE THIS WINDOW.")
                    Console.ReadLine() |> ignore
                    Logging.Critical("User has chosen to launch game with default data.") ""
                defaultData
        else
            Logging.Info(sprintf "No %s file found, creating it." name) ""
            defaultData

