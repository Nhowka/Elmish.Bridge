namespace Elmish.Bridge.Browser
open Elmish.Browser.Navigation

[<RequireQualifiedAccess>]
module Bridge =
  /// Maps the `'client` message to a `Navigable<'client>` message
  let NavigableMapping = UserMsg
