module Tests.Routing

open Expecto

open Prelude
open Tablecloth
open Prelude.Tablecloth

open TestUtils.TestUtils

module PT = LibExecution.ProgramTypes
module PTParser = LibExecution.ProgramTypesParser
module RT = LibExecution.RuntimeTypes

open LibBackend.Routing

let sanitizeUrlPath =
  testMany
    "sanitizeUrlPath"
    sanitizeUrlPath
    [ ("//", "/")
      ("/foo//bar", "/foo/bar")
      ("/abc//", "/abc")
      ("/abc/", "/abc")
      ("/abc", "/abc")
      ("/", "/")
      ("/abcabc//xyz///", "/abcabc/xyz")
      ("", "/") ]

let ownerNameFromHost =
  testMany
    "ownerNameFromHost"
    (fun cn ->
      cn
      |> CanvasName.create
      |> LibBackend.Account.ownerNameFromCanvasName
      |> fun (on : OwnerName.T) -> on.ToString())
    [ ("test-something", "test"); ("test", "test"); ("test-many-hyphens", "test") ]

let routeVariables =
  testMany
    "routeVariables"
    routeVariables
    [ ("/user/:userid/card/:cardid", [ "userid"; "cardid" ]) ]

let routeInputVars =
  testMany2
    "routeInputVars"
    routeInputVars
    [ ("/hello/:name", "/hello/alice-bob", Some [ "name", RT.DStr "alice-bob" ])
      ("/hello/alice-bob", "/hello/", None)
      ("/user/:userid/card/:cardid",
       "/user/myid/card/0",
       Some [ "userid", RT.DStr "myid"; "cardid", RT.DStr "0" ])
      // matche when same length
      ("/a/:b/c/d", "/a/b/c/d", Some [ "b", RT.DStr "b" ])
      // Succeeds with no vars
      ("/a/b/c/d", "/a/b/c/d", Some [])
      ("/a/b/c/d", "/a/b/c", None)
      ("/a/:b/c/d", "/a/b/c", None)
      // too short, fails
      ("/:a/b", "/a/b/c", None)
      // too long, fails
      ("/a/b/c/d", "/a/b/c", None)
      // trailing wildcard matches everything after
      ("/a/:b", "/a/b/c/d", Some [ "b", RT.DStr "b/c/d" ])
      ("/:a/:b/:c",
       "/a/b/c/d/e",
       Some [ "a", RT.DStr "a"; "b", RT.DStr "b"; "c", RT.DStr "c/d/e" ])
      // Incorrect last segment, fails
      ("/a/:b/c/d", "/a/b/c/e", None)
      // Doesn't crash
      ("/", "/a/b/c/d", None)
      // as the colon does not denote a variable, this is actually a malformed
      // route as `:` is reserved in the URL alphabet and thus we could never
      // receive a path that matches it
      ("/letters:var", "lettersextra", None) ]

let requestPathMatchesRoute =
  testMany2
    "requestPathMatchesRoute"
    requestPathMatchesRoute
    [ ("/user/:userid/card/:cardid", "/user/myid/card/0", true)
      ("/user/%/card/%", "/user/myid/card/0", true) // using pg wildcards
      ("/api/create-token", "/api-create_token", false)
      ("/%", "//.some-spam-address", true) ]

let five = PT.EInteger(gid (), 5)

let filterMatchingPatternsBySpecificity =
  testMany
    "filterMatchingPatternsBySpecificity"
    (fun routes ->
      routes
      |> List.map (fun r -> testHttpRouteHandler r "GET" five)
      |> filterMatchingHandlersBySpecificity
      |> List.map (fun h -> PTParser.Handler.Spec.toName h.spec))
    // concrete over wild
    [ ([ "/:foo"; "/a" ], [ "/a" ])
      // wild over nothing
      ([ "/a/:foo"; "/a" ], [ "/a/:foo" ])
      // differing number of segments
      ([ "/:first"; "/:first/:second" ], [ "/:first/:second" ])
      // lengthy abcdef wildcard
      ([ "/:a/b/c/d/:e/:f"; "/:a/b/c/:d/e/f" ], [ "/:a/b/c/d/:e/:f" ])
      // same length, diff # of wildcards
      ([ "/a/:b/:c"; "/:a/b/c" ], [ "/a/:b/:c" ])
      // same length, same # of wildcards
      ([ "/:a/b/c"; "/a/:b/c"; "/a/b/:c" ], [ "/a/b/:c" ])
      // multiple returned (note: test relies on ordering, though there's no reason for the ordering)
      ([ "/:first"; "/:first/:second"; "/:foo/:bar" ],
       [ "/:foo/:bar"; "/:first/:second" ]) ]

let filterInvalidHandlers =
  testMany2
    "filterInvalidHandlers"
    (fun path routes ->
      routes
      |> List.map (fun r -> testHttpRouteHandler r "GET" five)
      |> filterInvalidHandlerMatches path
      |> List.map (fun h -> PTParser.Handler.Spec.toName h.spec))
    // mismatch is filtered out
    [ ("/", [ "/:first" ], [])
      // mismatch is filtered out but root is left
      ("/", [ "/:first"; "/" ], [ "/" ]) ]

let filterMatchingHandlers =
  testMany2
    "filterMatchingHandlers"
    filterInvalidHandlerMatches
    // incomplete handler is filtered without throwing
    [ (let filled = testHttpRouteHandler "/:foo" "GET" five
       let emptyHttp = testHttpRouteHandler "" "" five
       ("/a", [ filled; emptyHttp ], [ filled ])) ]

let canvasNameFromHost =
  testManyTask
    "canvasNameFromHost"
    (fun h ->
      h |> canvasNameFromHost |> Task.map (Option.map (fun cn -> cn.ToString())))
    [ ("test-something.builtwithdark.com", Some "test-something")
      ("some-canvas.builtwithdark.localhost", Some "some-canvas")
      ("builtwithdark.localhost", Some "builtwithdark")
      ("some-canvas.darkcustomdomain.com", Some "some-canvas")
      ("www.microsoft.com", None) ]

let tests =
  testList
    "routing"
    [ sanitizeUrlPath
      ownerNameFromHost
      routeVariables
      routeInputVars
      requestPathMatchesRoute
      filterMatchingPatternsBySpecificity
      filterInvalidHandlers
      filterMatchingHandlers
      canvasNameFromHost ]
