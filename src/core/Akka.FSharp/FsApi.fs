﻿namespace Akka.FSharp

open Akka.Actor
open System
open Microsoft.FSharp.Quotations
open Microsoft.FSharp.Linq.QuotationEvaluation


[<AutoOpen>]
module Actors =

    /// <summary>
    /// Unidirectional send operator. 
    /// Sends a message object directly to actor tracked by actorRef. 
    /// </summary>
    let inline (<!) (actorRef : #ICanTell) (msg : obj) : unit = actorRef.Tell(msg, ActorCell.GetCurrentSelfOrNoSender())

    /// <summary> 
    /// Bidirectional send operator. Sends a message object directly to actor 
    /// tracked by actorRef and awaits for response send back from corresponding actor. 
    /// </summary>
    let inline (<?) (tell : #ICanTell) (msg : obj) = tell.Ask msg |> Async.AwaitTask

    /// Pipes an output of asynchronous expression directly to the recipients mailbox.
    let pipeTo (computation : Async<'T>) (recipient : ICanTell) (sender : ActorRef) : unit = 
        let success (result : 'T) : unit = recipient.Tell(result, sender)
        let failure (err : exn) : unit = recipient.Tell(Status.Failure(err), sender)
        Async.StartWithContinuations(computation, success, failure, failure)

    /// Pipe operator which sends an output of asynchronous expression directly to the recipients mailbox.
    let inline (|!>) (computation : Async<'T>) (recipient : ICanTell) = pipeTo computation recipient ActorRef.NoSender

    /// Pipe operator which sends an output of asynchronous expression directly to the recipients mailbox
    let inline (<!|) (recipient : ICanTell) (computation : Async<'T>) = pipeTo computation recipient ActorRef.NoSender

    type IO<'T> =
        | Input

    /// <summary>
    /// Exposes an Akka.NET actor APi accessible from inside of F# continuations -> <see cref="Cont{'In, 'Out}" />
    /// </summary>
    [<Interface>]
    type Actor<'Message> = 
        inherit ActorRefFactory
        inherit ICanWatch
    
        /// <summary>
        /// Explicitly retrieves next incoming message from the mailbox.
        /// </summary>
        abstract Receive : unit -> IO<'Message>
    
        /// <summary>
        /// Gets <see cref="ActorRef" /> for the current actor.
        /// </summary>
        abstract Self : ActorRef
    
        /// <summary>
        /// Gets the current actor context.
        /// </summary>
        abstract Context : IActorContext
    
        /// <summary>
        /// Returns a sender of current message or <see cref="ActorRef.NoSender" />, if none could be determined.
        /// </summary>
        abstract Sender : unit -> ActorRef
    
        /// <summary>
        /// Explicit signalization of unhandled message.
        /// </summary>
        abstract Unhandled : 'Message -> unit
    
        /// <summary>
        /// Lazy logging adapter. It won't be initialized until logging function will be called. 
        /// </summary>
        abstract Log : Lazy<Akka.Event.LoggingAdapter>

    [<AbstractClass>]
    type Actor() = 
        inherit UntypedActor()
 
    /// <summary>
    /// Returns an instance of <see cref="ActorSelection" /> for specified path. 
    /// If no matching receiver will be found, a <see cref="ActorRef.NoSender" /> instance will be returned. 
    /// </summary>
    let inline select (path : string) (selector : ActorRefFactory) : ActorSelection = selector.ActorSelection path
        
    /// Gives access to the next message throu let! binding in actor computation expression.
    type Cont<'In, 'Out> = 
        | Func of ('In -> Cont<'In, 'Out>)
        | Return of 'Out

    /// The builder for actor computation expression.
    type ActorBuilder() = 
    
        /// Binds the next message.
        member __.Bind(m : IO<'In>, f : 'In -> _) = Func(fun m -> f m)
    
        /// Binds the result of another actor computation expression.
        member this.Bind(x : Cont<'In, 'Out1>, f : 'Out1 -> Cont<'In, 'Out2>) : Cont<'In, 'Out2> = 
            match x with
            | Func fx -> Func(fun m -> this.Bind(fx m, f))
            | Return v -> f v
    
        member __.ReturnFrom(x) = x
        member __.Return x = Return x
        member __.Zero() = Return()
    
        member this.TryWith(f : unit -> Cont<'In, 'Out>, c : exn -> Cont<'In, 'Out>) : Cont<'In, 'Out> = 
            try 
                true, f()
            with ex -> false, c ex
            |> function 
            | true, Func fn -> Func(fun m -> this.TryWith((fun () -> fn m), c))
            | _, v -> v
    
        member this.TryFinally(f : unit -> Cont<'In, 'Out>, fnl : unit -> unit) : Cont<'In, 'Out> = 
            try 
                match f() with
                | Func fn -> Func(fun m -> this.TryFinally((fun () -> fn m), fnl))
                | r -> 
                    fnl()
                    r
            with ex -> 
                fnl()
                reraise()
    
        member this.Using(d : #IDisposable, f : _ -> Cont<'In, 'Out>) : Cont<'In, 'Out> = 
            this.TryFinally((fun () -> f d), 
                            fun () -> 
                                if d <> null then d.Dispose())
    
        member this.While(condition : unit -> bool, f : unit -> Cont<'In, unit>) : Cont<'In, unit> = 
            if condition() then 
                match f() with
                | Func fn -> 
                    Func(fun m -> 
                        fn m |> ignore
                        this.While(condition, f))
                | v -> this.While(condition, f)
            else Return()
    
        member __.For(source : 'Iter seq, f : 'Iter -> Cont<'In, unit>) : Cont<'In, unit> = 
            use e = source.GetEnumerator()
        
            let rec loop() = 
                if e.MoveNext() then 
                    match f e.Current with
                    | Func fn -> 
                        Func(fun m -> 
                            fn m |> ignore
                            loop())
                    | r -> loop()
                else Return()
            loop()
    
        member __.Delay(f : unit -> Cont<_, _>) = f
        member __.Run(f : unit -> Cont<_, _>) = f()
        member __.Run(f : Cont<_, _>) = f
    
        member this.Combine(f : unit -> Cont<'In, _>, g : unit -> Cont<'In, 'Out>) : Cont<'In, 'Out> = 
            match f() with
            | Func fx -> Func(fun m -> this.Combine((fun () -> fx m), g))
            | Return _ -> g()
    
        member this.Combine(f : Cont<'In, _>, g : unit -> Cont<'In, 'Out>) : Cont<'In, 'Out> = 
            match f with
            | Func fx -> Func(fun m -> this.Combine(fx m, g))
            | Return _ -> g()
    
        member this.Combine(f : unit -> Cont<'In, _>, g : Cont<'In, 'Out>) : Cont<'In, 'Out> = 
            match f() with
            | Func fx -> Func(fun m -> this.Combine((fun () -> fx m), g))
            | Return _ -> g
    
        member this.Combine(f : Cont<'In, _>, g : Cont<'In, 'Out>) : Cont<'In, 'Out> = 
            match f with
            | Func fx -> Func(fun m -> this.Combine(fx m, g))
            | Return _ -> g

    type FunActor<'Message, 'Returned>(actor : Actor<'Message> -> Cont<'Message, 'Returned>) as this = 
        inherit Actor()
    
        let mutable state = 
            let self' = this.Self
            let context = UntypedActor.Context :> IActorContext
            actor { new Actor<'Message> with
                        member __.Receive() = Input
                        member __.Self = self'
                        member __.Context = context
                        member __.Sender() = this.Sender()
                        member __.Unhandled msg = this.Unhandled msg
                        member __.ActorOf(props, name) = context.ActorOf(props, name)
                        member __.ActorSelection(path : string) = context.ActorSelection(path)
                        member __.ActorSelection(path : ActorPath) = context.ActorSelection(path)
                        member __.Watch(aref:ActorRef) = context.Watch aref
                        member __.Unwatch(aref:ActorRef) = context.Unwatch aref
                        member __.Log = lazy (Akka.Event.Logging.GetLogger(context)) }
    
        new(actor : Expr<Actor<'Message> -> Cont<'Message, 'Returned>>) = FunActor(actor.Compile () ())
        member __.Sender() : ActorRef = base.Sender
        member __.Unhandled msg = base.Unhandled msg
        override x.OnReceive msg = 
            match state with
            | Func f -> state <- f (msg :?> 'Message)
            | Return _ -> x.PostStop()

    /// Builds an actor message handler using an actor expression syntax.
    let actor = ActorBuilder()
    
[<AutoOpen>]
module Logging = 
    open Akka.Event

    /// Logs a message using configured Akka logger.
    let log (level : LogLevel) (mailbox : Actor<'Message>) (msg : string) : unit = 
        let logger = mailbox.Log.Force()
        logger.Log(level, msg)
    
    /// Logs a message at Debug level using configured Akka logger.
    let inline logDebug mailbox msg = log LogLevel.DebugLevel mailbox msg
    
    /// Logs a message at Info level using configured Akka logger.
    let inline logInfo mailbox msg = log LogLevel.InfoLevel mailbox msg
    
    /// Logs a message at Warning level using configured Akka logger.
    let inline logWarning mailbox msg = log LogLevel.WarningLevel mailbox msg
    
    /// Logs a message at Error level using configured Akka logger. 
    let inline logError mailbox msg = log LogLevel.ErrorLevel mailbox msg
    
    /// Logs an exception message at Error level using configured Akka logger.
    let inline logException mailbox (e : exn) = log LogLevel.ErrorLevel mailbox (e.Message)

    open Printf
    
    let inline private doLogf level (mailbox: Actor<'Message>) msg = 
        mailbox.Log.Value.Log(level, msg) |> ignore

    /// Logs a message using configured Akka logger.
    let inline logf (level : LogLevel) (mailbox : Actor<'Message>) = 
        kprintf (doLogf level mailbox)
     
    /// Logs a message at Debug level using configured Akka logger.
    let inline logDebugf mailbox = kprintf (doLogf LogLevel.DebugLevel mailbox)
    
    /// Logs a message at Info level using configured Akka logger.
    let inline logInfof mailbox = kprintf (doLogf LogLevel.InfoLevel mailbox)
    
    /// Logs a message at Warning level using configured Akka logger.
    let inline logWarningf mailbox = kprintf (doLogf LogLevel.WarningLevel mailbox)
    
    /// Logs a message at Error level using configured Akka logger. 
    let inline logErrorf mailbox = kprintf (doLogf LogLevel.ErrorLevel mailbox)


module Linq = 
    open System.Linq.Expressions
    
    let (|Lambda|_|) (e : Expression) = 
        match e with
        | :? LambdaExpression as l -> Some(l.Parameters, l.Body)
        | _ -> None
    
    let (|Call|_|) (e : Expression) = 
        match e with
        | :? MethodCallExpression as c -> Some(c.Object, c.Method, c.Arguments)
        | _ -> None
    
    let (|Method|) (e : System.Reflection.MethodInfo) = e.Name
    
    let (|Invoke|_|) = 
        function 
        | Call(o, Method("Invoke"), _) -> Some o
        | _ -> None
    
    let (|Ar|) (p : System.Collections.ObjectModel.ReadOnlyCollection<Expression>) = Array.ofSeq p
    
    type Expression = 
        static member ToExpression(f : System.Linq.Expressions.Expression<System.Func<FunActor<'Message, 'v>>>) = 
            match f with
            | Lambda(_, Invoke(Call(null, Method "ToFSharpFunc", Ar [| Lambda(_, p) |]))) -> 
                Expression.Lambda(p, [||]) :?> System.Linq.Expressions.Expression<System.Func<FunActor<'Message, 'v>>>
            | _ -> failwith "Doesn't match"

module Serialization = 
    open Nessos.FsPickler
    open Akka.Serialization
    
    type ExprSerializer(system) = 
        inherit Serializer(system)
        let fsp = FsPickler.CreateBinary()
        override __.Identifier = 9
        override __.IncludeManifest = true
        
        override __.ToBinary(o) = 
            use stream = new System.IO.MemoryStream()
            fsp.Serialize(o.GetType(), stream, o)
            stream.ToArray()
        
        override __.FromBinary(bytes, t) = 
            use stream = new System.IO.MemoryStream(bytes)
            fsp.Deserialize(t, stream)

[<RequireQualifiedAccess>]
module Configuration = 

    /// Parses provided HOCON string into a valid Akka configuration object.
    let parse = Akka.Configuration.ConfigurationFactory.ParseString

    /// Returns default Akka configuration.
    let defaultConfig = Akka.Configuration.ConfigurationFactory.Default

    /// Loads Akka configuration from the project's .config file.
    let load = Akka.Configuration.ConfigurationFactory.Load
    
module internal OptionHelper =
    
    let optToNullable = function
        | Some x -> Nullable x
        | None -> Nullable()

type Strategy = 

    /// <summary>
    /// Returns a supervisor strategy appliable only to child actor which faulted during execution.
    /// </summary>
    /// <param name="decider">Used to determine a actor behavior response depending on exception occurred.</param>
    static member OneForOne (decider : exn -> Directive) : SupervisorStrategy = 
        upcast OneForOneStrategy(System.Func<_, _>(decider))
   
    /// <summary>
    /// Returns a supervisor strategy appliable only to child actor which faulted during execution.
    /// </summary>
    /// <param name="retries">Defines a number of times, an actor could be restarted. If it's a negative value, there is not limit.</param>
    /// <param name="timeout">Defines time window for number of retries to occur.</param>
    /// <param name="decider">Used to determine a actor behavior response depending on exception occurred.</param>
    static member OneForOne (decider : exn -> Directive, ?retries : int, ?timeout : TimeSpan)  : SupervisorStrategy = 
        upcast OneForOneStrategy(OptionHelper.optToNullable retries, OptionHelper.optToNullable timeout, System.Func<_, _>(decider))
    
    /// <summary>
    /// Returns a supervisor strategy appliable to each supervised actor when any of them had faulted during execution.
    /// </summary>
    /// <param name="decider">Used to determine a actor behavior response depending on exception occurred.</param>
    static member AllForOne (decider : exn -> Directive) : SupervisorStrategy = 
        upcast AllForOneStrategy(System.Func<_, _>(decider))
    
    /// <summary>
    /// Returns a supervisor strategy appliable to each supervised actor when any of them had faulted during execution.
    /// </summary>
    /// <param name="retries">Defines a number of times, an actor could be restarted. If it's a negative value, there is not limit.</param>
    /// <param name="timeout">Defines time window for number of retries to occur.</param>
    /// <param name="decider">Used to determine a actor behavior response depending on exception occurred.</param>
    static member AllForOne (decider : exn -> Directive, ?retries : int, ?timeout : TimeSpan) : SupervisorStrategy = 
        upcast AllForOneStrategy(OptionHelper.optToNullable retries, OptionHelper.optToNullable timeout, System.Func<_, _>(decider))
        
module System = 
    /// Creates an actor system with remote deployment serialization enabled.
    let create (name : string) (config : Akka.Configuration.Config) : ActorSystem = 
        let system = ActorSystem.Create(name, config)
        let serializer = Serialization.ExprSerializer(system :?> ExtendedActorSystem)
        system.Serialization.AddSerializer(serializer)
        system.Serialization.AddSerializationMap(typeof<Expr>, serializer)
        system
        
[<AutoOpen>]
module Spawn =

    type SpawnOption = 
        | Deploy of Deploy
        | Router of Akka.Routing.RouterConfig
        | SupervisorStrategy of SupervisorStrategy
        | Dispatcher of string
        | Mailbox of string

    let rec applySpawnOptions (props : Props) (opt : SpawnOption list) : Props = 
        match opt with
        | [] -> props
        | h :: t -> 
            let p = 
                match h with
                | Deploy d -> props.WithDeploy d
                | Router r -> props.WithRouter r
                | SupervisorStrategy s -> props.WithSupervisorStrategy s
                | Dispatcher d -> props.WithDispatcher d
                | Mailbox m -> props.WithMailbox m
            applySpawnOptions p t

    /// <summary>
    /// Spawns an actor using specified actor computation expression, using an Expression AST.
    /// The actor code can be deployed remotely.
    /// </summary>
    /// <param name="actorFactory">Either actor system or parent actor</param>
    /// <param name="name">Name of spawned child actor</param>
    /// <param name="expr">F# expression compiled down to receive function used by actor for response for incoming request</param>
    /// <param name="options">List of options used to configure actor creation</param>
    let spawne (actorFactory : ActorRefFactory) (name : string) (expr : Expr<Actor<'Message> -> Cont<'Message, 'Returned>>) 
        (options : SpawnOption list) : ActorRef = 
        let e = Linq.Expression.ToExpression(fun () -> new FunActor<'Message, 'Returned>(expr))
        let props = applySpawnOptions (Props.Create e) options
        actorFactory.ActorOf(props, name)

    /// <summary>
    /// Spawns an actor using specified actor computation expression, with custom actor Props settings.
    /// The actor can only be used locally. 
    /// </summary>
    /// <param name="actorFactory">Either actor system or parent actor</param>
    /// <param name="name">Name of spawned child actor</param>
    /// <param name="f">Used by actor for handling response for incoming request</param>
    /// <param name="options">List of options used to configure actor creation</param>
    let spawnOpt (actorFactory : ActorRefFactory) (name : string) (f : Actor<'Message> -> Cont<'Message, 'Returned>) 
        (options : SpawnOption list) : ActorRef = 
        let e = Linq.Expression.ToExpression(fun () -> new FunActor<'Message, 'Returned>(f))
        let props = applySpawnOptions (Props.Create e) options
        actorFactory.ActorOf(props, name)

    /// <summary>
    /// Spawns an actor using specified actor computation expression.
    /// The actor can only be used locally. 
    /// </summary>
    /// <param name="actorFactory">Either actor system or parent actor</param>
    /// <param name="name">Name of spawned child actor</param>
    /// <param name="f">Used by actor for handling response for incoming request</param>
    let spawn (actorFactory : ActorRefFactory) (name : string) (f : Actor<'Message> -> Cont<'Message, 'Returned>) : ActorRef = 
        spawnOpt actorFactory name f []

    /// <summary>
    /// Wraps provided function with actor behavior. 
    /// It will be invoked each time, an actor will receive a message. 
    /// </summary>
    let actorOf (fn : 'Message -> unit) (mailbox : Actor<'Message>) : Cont<'Message, 'Returned> = 
        let rec loop() = 
            actor { 
                let! msg = mailbox.Receive()
                fn msg
                return! loop()
            }
        loop()

    /// <summary>
    /// Wraps provided function with actor behavior. 
    /// It will be invoked each time, an actor will receive a message. 
    /// </summary>
    let actorOf2 (fn : Actor<'Message> -> 'Message -> unit) (mailbox : Actor<'Message>) : Cont<'Message, 'Returned> = 
        let rec loop() = 
            actor { 
                let! msg = mailbox.Receive()
                fn mailbox msg
                return! loop()
            }
        loop()

[<AutoOpen>]
module Inbox =

    /// <summary>
    /// Creates an actor-like object, which could be interrogated from the outside. 
    /// Usually it's used to spy on other actors lifecycle.
    /// Most of the inbox methods works in thread-blocking manner.
    /// </summary>
    let inbox (system : ActorSystem) : Inbox = Inbox.Create system

    /// <summary> 
    /// Receives a next message sent to the inbox. This is a blocking operation.
    /// Returns None if timeout occurred or message is incompatible with expected response type.
    /// </summary>
    let receive (timeout : TimeSpan) (i : Inbox) : 'Message option = 
        try 
            Some(i.Receive(timeout) :?> 'Message)
        with _ -> None

    /// <summary>
    /// Receives a next message sent to the inbox, which satisfies provided predicate. 
    /// This is a blocking operation. Returns None if timeout occurred or message 
    /// is incompatible with expected response type.
    /// <summary>
    let filterReceive (timeout : TimeSpan) (predicate : 'Message -> bool) (i : Inbox) : 'Message option = 
        try 
            let r = 
                i.ReceiveWhere(Predicate<obj>(fun (o : obj) -> 
                                   match o with
                                   | :? 'Message as message -> predicate message
                                   | _ -> false), timeout)
            Some(r :?> 'Message)
        with _ -> None

    /// <summary>
    /// Awaits in async block fora  next message sent to the inbox. 
    /// Returns None if message is incompatible with expected response type.
    /// </summary>
    let asyncReceive (i : Inbox) : Async<'Message option> = 
        async { 
            let! r = i.ReceiveAsync() |> Async.AwaitTask
            return match r with
                   | :? 'Message as message -> Some message
                   | _ -> None
        }

[<AutoOpen>]
module Watchers =

    /// <summary>
    /// Orders a <paramref name="watcher"/> to monitor an actor targeted by provided <paramref name="subject"/>.
    /// When an actor refered by subject dies, a watcher should receive a <see cref="Terminated"/> message.
    /// </summary>
    let monitor (subject: ActorRef) (watcher: ICanWatch) : ActorRef = watcher.Watch subject

    /// <summary>
    /// Orders a <paramref name="watcher"/> to stop monitoring an actor refered by provided <paramref name="subject"/>.
    /// </summary>
    let demonitor (subject: ActorRef) (watcher: ICanWatch) : ActorRef = watcher.Unwatch subject

[<AutoOpen>]
module EventStreaming =

    /// <summary>
    /// Subscribes an actor reference to target channel of the provided event stream.
    /// </summary>
    let subscribe (channel: System.Type) (ref: ActorRef) (eventStream: Akka.Event.EventStream) : bool = eventStream.Subscribe(ref, channel)

    /// <summary>
    /// Unubscribes an actor reference from target channel of the provided event stream.
    /// </summary>
    let unsubscribe (channel: System.Type) (ref: ActorRef) (eventStream: Akka.Event.EventStream) : bool = eventStream.Unsubscribe(ref, channel)

    /// <summary>
    /// Publishes an event on the provided event stream. Event channel is resolved from event's type.
    /// </summary>
    let pubblish (event: 'Event) (eventStream: Akka.Event.EventStream) : unit = eventStream.Publish event

[<AutoOpen>]
module Scheduler =

    let private taskContinuation (task: System.Threading.Tasks.Task) : unit =
        match task.IsFaulted with
        | true -> raise task.Exception
        | _ -> ()

    /// <summary>
    /// Schedules a function to be invoked repeatedly in the provided time intervals. 
    /// </summary>
    /// <param name="after">Initial delay to first function call.</param>
    /// <param name="every">Interval.</param>
    /// <param name="fn">Function called by the scheduler.</param>
    /// <param name="scheduler"></param>
    let schedule (after: TimeSpan) (every: TimeSpan) (fn: unit -> unit) (scheduler: Scheduler): Async<unit> =
        let action = Action fn
        Async.AwaitTask (scheduler.Schedule(after, every, action).ContinueWith taskContinuation)
    
    /// <summary>
    /// Schedules a single function call using specified sheduler.
    /// </summary>
    /// <param name="after">Delay before calling the function.</param>
    /// <param name="fn">Function called by the scheduler.</param>
    /// <param name="scheduler"></param>
    let scheduleOnce (after: TimeSpan) (fn: unit -> unit) (scheduler: Scheduler): Async<unit> =
        let action = Action fn
        Async.AwaitTask (scheduler.ScheduleOnce(after, action).ContinueWith taskContinuation)

    /// <summary>
    /// Schedules a <paramref name="message"/> to be sent to the provided <paramref name="receiver"/> in specified time intervals.
    /// </summary>
    /// <param name="after">Initial delay to first function call.</param>
    /// <param name="every">Interval.</param>
    /// <param name="message">Message to be sent to the receiver by the scheduler.</param>
    /// <param name="receiver">Message receiver.</param>
    /// <param name="scheduler"></param>
    let scheduleTell (after: TimeSpan) (every: TimeSpan) (message: 'Message) (receiver: ActorRef) (scheduler: Scheduler): Async<unit> =
        Async.AwaitTask (scheduler.Schedule(after, every, receiver, message).ContinueWith taskContinuation)
    
    /// <summary>
    /// Schedules a single <paramref name="message"/> send to the provided <paramref name="receiver"/>.
    /// </summary>
    /// <param name="after">Delay before sending a message.</param>
    /// <param name="message">Message to be sent to the receiver by the scheduler.</param>
    /// <param name="receiver">Message receiver.</param>
    /// <param name="scheduler"></param>
    let scheduleTellOnce (after: TimeSpan) (message: 'Message) (receiver: ActorRef) (scheduler: Scheduler): Async<unit> =
        Async.AwaitTask (scheduler.ScheduleOnce(after, receiver, message).ContinueWith taskContinuation)
