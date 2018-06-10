namespace Elmish.Remoting.Browser
open Elmish.Browser.Navigation

[<RequireQualifiedAccess>]
module RemoteProgram =
  /// Maps the `'client` message to a `Navigable<'client>` message
  let NavigableMapping = UserMsg
