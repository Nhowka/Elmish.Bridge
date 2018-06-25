# Elmish.Bridge

Formely Elmish.Remoting. This library creates a bridge between server and client using websockets so you can keep the same model-view-update mindset to create the server side model.

## Available Packages:

| Library  | Version |
| -------- | ------- |
| Elmish.Bridge.Client  | [![Nuget](https://img.shields.io/nuget/v/Elmish.Bridge.Client.svg?colorB=green)](https://www.nuget.org/packages/Elmish.Bridge.Client) |
| Elmish.Bridge.Suave  | [![Nuget](https://img.shields.io/nuget/v/Elmish.Bridge.Suave.svg?colorB=green)](https://www.nuget.org/packages/Elmish.Bridge.Suave)  |
| Elmish.Bridge.Giraffe  | [![Nuget](https://img.shields.io/nuget/v/Elmish.Bridge.Giraffe.svg?colorB=green)](https://www.nuget.org/packages/Elmish.Bridge.Giraffe)  |
| Elmish.Bridge.HMR  | [![Nuget](https://img.shields.io/nuget/v/Elmish.Bridge.HMR.svg?colorB=green)](https://www.nuget.org/packages/Elmish.Bridge.HMR)  |
| Elmish.Bridge.Browser  | [![Nuget](https://img.shields.io/nuget/v/Elmish.Bridge.Browser.svg?colorB=green)](https://www.nuget.org/packages/Elmish.Bridge.Browser)  |

## Why?

The MVU approach made programming client-side SPA really fun and fast for me. The idea of having a model updated in a predictable way, with a view just caring about what was in the model just clicked, and making a server-side with just consumable services made everything different and I felt like I was making two entirely different programs. I came up with this project so I can have the same fun when creating the server.

## Drawbacks

You are now just passing messages between the server and the client. Stuff like authorization, headers or cookies are not there anymore. You have a stateful connection between the client while it's open, but if it gets disconnected the server will lose all the state it had.
That can be mitigated by sending a message to the client when it reestabilishes the connection, so it knows that the server is there again. You can then send all the information that the server needs to feel like the connection was never broken in the first place.

tl;dr You have to relearn the server-side part

## How to use it?

This is Elmish on server with a bridge to the client. You can learn about Elmish [here](https://elmish.github.io/). This assumes that you know how to use Elmish on the client-side.

### Shared

I recommend to keep the messages and the endpoint on a shared file; you don't need to keep the model if you decide that you want the server and client to have different models.

```fsharp
// Messages processed on the server
type ServerMsg =
    |...
//Messages processed on the client
type ClientMsg =
    |...
module Shared =
    let endpoint = "/socket"
```

The server and the client can return both kind of messages: `S ServerMsg` to define the server and `C ClientMsg` to the client kind. The `update` function now returns a `'model * Cmd<Msg<ServerMsg,ClientMsg>>`.

### Client

What's different? Well, now you can send messages to and get messages from the server. The `update` function that was `'msg -> 'model -> 'model * Cmd<'msg>` now is a `'client -> 'model -> 'model * Cmd<Msg<'server,'client>>` and that is incompatible with the Elmish's `mkProgram`, so you can use the `bridge` function to get a computation expression to configure your client. You can pass functions taking the Elmish program with the `simple` operation if it results in the same message type or `mapped` otherwise, taking a mapping function before the program function.

```fsharp
open Elmish
open Elmish.Bridge

bridge init update view {
  simple (Program.withReact "elmish-app")
  at Shared.endpoint
}
```

### Server

Now you can use the MVU approach on the server, minus the V. That still is just a client thing. There is one configured for Suave on the `Elmish.Bridge.Suave` package and another for Giraffe and Saturn on the `Elmish.Bridge.Giraffe` package. Create a new server using `bridge init update`.

- Suave

```fsharp
open Elmish
open Elmish.Bridge

let server =
  bridge init update {
    at Shared.endpoint
  }

let webPart =
  choose [
    server
    Filters.path "/" >=> Files.browseFileHome "index.html"
  ]
startWebServer config webPart
```

- Giraffe

```fsharp
open Elmish
open Elmish.Bridge

let server =
  bridge init update {
    at Shared.endpoint
  }

let webApp =
  choose [
    server
    route "/" >=> htmlFile "/pages/index.html" ]

let configureApp (app : IApplicationBuilder) =
  app
    .UseWebSockets()
    .UseGiraffe webApp

let configureServices (services : IServiceCollection) =
  services.AddGiraffe() |> ignore

WebHostBuilder()
  .UseKestrel()
  .Configure(Action<IApplicationBuilder> configureApp)
  .ConfigureServices(configureServices)
  .Build()
  .Run()
```

- Saturn

```fsharp
open Elmish
open Elmish.Bridge

let server =
  bridge init update {
    at Shared.endpoint
  }

let webApp =
  choose [
    server
    route "/" >=> htmlFile "/pages/index.html" ]

let app =
  application {
    router server
    disable_diagnostics
    app_config Giraffe.useWebSockets
    url uri
  }

run app
```

### ServerHub

WebSockets are even better when you use that communication power to send messages between clients. No library would be complete without help to broadcast messages or send a message to an specific client. That's where the `ServerHub` comes in! It is a very simple class that worries about keeping the information about all the connected clients so you don't have to.

Create a new `ServerHub` using:

```fsharp
let hub = ServerHub.New()
```

then you can use it on your server:

```fsharp
bridge init update {
  serverHub hub
  at Shared.endpoint
}
```

It has three functions:
* `Broadcast`

    Send a message to anyone connected:
    ```fsharp
      hub.Broadcast (C (NewMessage "Hello, everyone!"))
    ```

* `SendIf`

    Send a message to anyone whose model satisfies a predicate.
    ```fsharp
      hub.SendIf (function {Gender = Female} -> true | _ -> false) (C (NewMessage "Hello, ladies!"))
    ```

* `GetModels`

    Gets a list with all models of all connected clients.
    ```fsharp
      let users = hub.GetModels() |> List.map (function {Name = n} -> n)
      hub.Broadcast (C (ConnectedUsers users))
    ```

These functions were enough when creating a simple chat, but let me know if you feel limited having only them!

### HMR / Browser

The HMR and Navigation module creates a new kind of message , but you can use `Elmish.Bridge.HMR` / `Elmish.Bridge.Broswer` helpers on the `mapped` operation to create a compatible `ClientProgram`:

```fsharp
open Elmish
open Elmish.Bridge
open Elmish.Bridge.Browser
open Elmish.Browser.Navigation
open Elmish.HMR
open Elmish.Bridge.HMR

bridge init update view {
  mapped Bridge.HMRMsgMapping Program.withHMR
  mapped Bridge.NavigableMapping (Program.toNavigable Pages.urlParser urlUpdate)
  simple (Program.withReact "elmish-app")
  at Shared.endpoint
}
```

## All operations

A full description and explanation about the arguments can be found on the code and tooltip on the editor

### Client

- `simple` : Pass a function that takes an Elmish Program that don't change the message type
- `mapped` : Pass a function that takes a message mapping and a function taking the Elmish Program that changes the message according to the mapping
- `at` : Defines the endpoint where the connection will be made
- `whenDown` : Defines a message that will be dispatched when the connection is lost
- `sub` : Modified subscription that can send client and server messages. Takes `unit` instead of the model
- `runWith` : Defines the argument when the `init` function don't take `unit` as an argument

### Server

- `serverHub` : Defines the `ServerHub` used by the connection
- `sub` : Defines a subscription
- `whenDown` : Defines a message to be dispatched when the client disconnects
- `consoleTrace` : Log the messages and new models to the console
- `at` : Defines the endpoint where the server will listen to connections
- `runWith` : Defines the argument when the `init` function don't take `unit` as an argument

## Anything more?

This documentation is on an early stage, if you have any questions feel free to open an issue or PR so we can have it in a good shape. You can check a test project [here](https://github.com/Nhowka/TestRemoting). I hope you enjoy using it!
