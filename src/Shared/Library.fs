namespace Elmish.Remoting

/// Shared type. Separates which messages are processed on the client or the server
type Msg<'server,'client> =
    | S of 'server
    | C of 'client



