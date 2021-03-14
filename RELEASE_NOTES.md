#### 5.0.0
* Use net5.0
* Use Elmish Program as base to the server loop

#### 3.2.1
* Inlines more functions to keep generics working

#### 3.2.0
* Supports newer Fable.SimpleJson and fable-compiler

#### 3.1.1
* Support callbacks to be called when the websocket is broken
* Cmd-like functions for easier usage

#### 3.0.3
* Compatible with Fable.Core 3 and Elmish 3
* Replaced Thoth.Json with Fable.SimpleJson
* Support for customizing the message the client sends and the server receives
* Detects non-ambiguous union cases and register them at the server, including tuples
* Fix subscription

#### 2.0.0
* Compatible with Fable 2

#### 1.2.4
* Fix the reconnection for real this time

#### 1.2.3
* Prevent unwanted eagerness

#### 1.2.2
* Reconnect when closed and also on errors

#### 1.2.1
* Prevents the hash from being on the url when using `url-polyfill`

#### 1.2.0
* Enables better control over the endpoint definition

#### 1.1.1
* Simplify implementators logic
* Uses a try-with on the Giraffe socket to always notify that the connection is closed

#### 1.1.0
* Makes ServerHub mockable (by @Zaid-Ajaj)
* Enable multiple bridge connections with named bridges

#### 1.0.1
* Solves a potential leak

#### 1.0.0
* Support a custom retry time
* Support a common configuration record
* Use the Elmish package instead of Fable.Elmish on the server

#### 0.10.10
* Register main type when running so it can be logged

#### 0.10.9
* Account for nested classes when registering the top-level update type

#### 0.10.8
* Account for nested classes and differences in naming on reflection

#### 0.10.7
* Even more tracing

#### 0.10.6
* More tracing

#### 0.10.5
* Public version of the new API

#### 0.10.4-alpha
* Use disconnection message with the same type as the top-level message

#### 0.10.3-alpha
* Support receiving inner messages so the server can work with a sub-set of the messages

#### 0.10.2-alpha
* Helper function to register server mappings

#### 0.10.1-alpha
* Be explicit about the separation on client and server messages on ServerHub

#### 0.10.0-alpha
* Less intrusive API

#### 0.9.10
* Support for '#'-s in the navigation address (by @cotyar)

#### 0.9.9
* Code documentation

#### 0.9.4
* Use PassGenerics on client CE-API

#### 0.9.3
* More consistent API

#### 0.9.2
* Test new API

#### 0.9.0
* Project renamed to Elmish.Bridge

#### 0.7.5
* Support longer messages

#### 0.7.2
* Republish on the same time due to manifest conflicts

#### 0.1.0-next
* Initial release
