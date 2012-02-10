﻿module Barb.Tests.Lib

open Barb.Compiler
open Xunit

[<Fact>] 
let ``should support hasIntersection`` () =
    let predicate = buildExpr<unit,bool> "Lib.hasIntersection ((1,2,3), (3,4,5))"
    let result = predicate ()
    Assert.True(result)

[<Fact>] 
let ``should properly return false on hasIntersection`` () =
    let predicate = buildExpr<unit,bool> "Lib.hasIntersection ((1,2,3), (4,5))"
    let result = predicate ()
    Assert.False(result)

[<Fact>] 
let ``should support union`` () =
    let predicate = buildExpr<unit,bool> "Lib.union ((1,2,3), (4,5)) = (1,2,3,4,5)"
    let result = predicate ()
    Assert.True(result)

[<Fact>] 
let ``should support intersection`` () =
    let predicate = buildExpr<unit,bool> "Lib.intersection ((1,2,3), (4,2,3)) = (2,3)"
    let result = predicate ()
    Assert.True(result)