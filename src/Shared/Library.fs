namespace Elmish.Bridge

/// Shared type. Separates which messages are processed on the client or the server
type Msg<'server,'client> =
    | S of 'server
    | C of 'client

[<RequireQualifiedAccess>]
module Cmd =

    open Elmish

    /// Maps an inner `Msg` using a server mapping and a client mapping to compose `update`s functions
    let remoteMap (serverMapping:'innerserver -> 'server) (clientMapping:'innerclient -> 'client) (cmd:Cmd<Msg<'innerserver,'innerclient>>) : Cmd<Msg<'server,'client>> =
        cmd |> Cmd.map (function C msg -> C (clientMapping msg) | S msg -> S (serverMapping msg))
