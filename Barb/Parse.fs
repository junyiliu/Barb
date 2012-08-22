﻿module Barb.Parse

// TODO: 
// Do ascii lookup for Num, instead of a dumb list
// Add a way for captureTypes to know if a Repeat node has been already seen
 
open System
open System.Text
open System.Text.RegularExpressions
open System.Reflection
open System.ComponentModel
open System.Linq
open System.Collections.Concurrent
open System.Collections.Generic

open Barb.Helpers
open Barb.Interop
open Barb.Representation

type BarbParsingException (message, index) = 
    inherit Exception (message)
    member t.Index = index

type StringWindow =
    struct
        // These are mutable to prevent property generation.
        val mutable Text: string 
        val mutable Offset: int
        new(text: string, offset: int) = { Text = text; Offset = offset }
    end
    with 
        member t.StartsWith (str: string) =            
            let text = t.Text
            let offset = t.Offset
            let textLen = t.Length
            let rec matches i res =
                if i > textLen - 1 then false
                else
                    let newres = res && (text.[offset + i] = str.[i])
                    match i, newres with | 0, _ -> newres | _, false -> newres | _ -> matches (i - 1) newres
            matches (str.Length - 1) true
        member t.Length = t.Text.Length - t.Offset
        member t.Subwindow start = StringWindow (t.Text, t.Offset + start)
        member t.Substring (index, len) = t.Text.Substring(t.Offset + index, len)
        member t.IndexOf (pattern: string) = max (t.Text.IndexOf(pattern, t.Offset) - t.Offset) -1
        member t.Item with get(x) = t.Text.[x + t.Offset]
        override t.ToString () = t.Text.Substring(t.Offset) 


type MatchReturn = (ExprTypes option * StringWindow) option

type DelimType =
    | Open
    | SCap of string
    | RCap of string


type SubexpressionType = 
    {
        Pattern: DelimType list
        Func: ExprRep list -> ExprRep
    }
    
type SubexpressionAndOffset = SubexpressionType * int

open System.Numerics

let whitespace = [| " "; "\r"; "\n"; "\t"; |]

let (|Num|_|) (text: StringWindow) : MatchReturn =
    let isnumchar c = c >= '0' && c <= '9' 
    let textStartAt = text.Offset
    let sb = new StringBuilder()
    if isnumchar text.[0] || (text.[0] = '.' && text.Length >= 2 && isnumchar text.[1]) then
        let rec inner = 
            function
            | i, dot when i >= text.Length -> i, dot
            | i, true when text.[i] = '.' -> i, true
            | i, false when text.[i] = '.' -> sb.Append(text.[i]) |> ignore; inner (i+1, true)
            | i, dotSeen when isnumchar text.[i] -> sb.Append(text.[i]) |> ignore; inner (i+1, dotSeen)
            | i, dot -> i, dot
        let resi, dot = inner (0, false)
        let tokenStr = 
            let resultStr = sb.ToString()
            if dot then match Double.TryParse(resultStr) with | true, num -> Obj num | _ -> Obj resultStr
            else match Int64.TryParse(resultStr) with | true, num -> Obj num | _ -> Obj resultStr
        let rest = text.Subwindow(resi)
        Some (Some (tokenStr), rest)
    else None
        
let (|CaptureString|_|) (b: char) (text: StringWindow) : MatchReturn = 
    if text.[0] = b then 
        let sb = new StringBuilder()
        let rec findSafeIndex i =
            if i >= text.Length then failwith "Quotes not matched"
            elif text.[i] = '\\' then findSafeIndex(i + 1)
            elif text.[i] = b && text.[i - 1] <> '\\' then 
                let tokenStr = Some (Obj (sb.ToString() :> obj))
                let rest = text.Subwindow(i + 1)
                Some (tokenStr, rest)
            else sb.Append(text.[i]) |> ignore; findSafeIndex (i + 1)
        findSafeIndex 1
    else None  

let (|CaptureUnknown|_|) (endTokens: string list) (text: StringWindow) : MatchReturn =
    let endIndices = 
        endTokens 
        |> List.map (fun e -> text.IndexOf e)
        |> List.filter (fun i -> i >= 0)
    match endIndices with
    | [] -> Some (Some (Unknown (text.ToString())), StringWindow("", 0))
    | list -> 
        let index = list |> List.min 
        let tokenText = text.Substring(0, index)
        let remainder = text.Subwindow(index)
        if index > 0 then 
            Some (Some (Unknown tokenText), remainder) 
        else None

let generateTuple (exprs: ExprRep list) : ExprRep = 
    let offset, length = exprRepListOffsetLength exprs in 
        { Offset = offset; Length = length; Expr = Tuple (exprs |> List.toArray) }

let generateLambda (exprs: ExprRep list) : ExprRep = 
    match exprs with 
    | { Expr = SubExpression(names) } :: contents :: [] ->
        let offset, length = exprRepListOffsetLength exprs
        let prms = names |> List.map (function | { Expr = Unknown n } -> n | other -> failwith (sprintf "Unexpected construct in lambda argument list: %A" other))
        { Offset = offset; Length = length; Expr = Lambda (prms, Map.empty, contents) }
    | list -> failwith (sprintf "Incorrect lambda binding syntax: %A" list)

let generateIfThenElse (exprs: ExprRep list) : ExprRep = 
    match exprs with
    | { Expr = SubExpression(ifexpr) } :: { Expr = SubExpression(thenexpr) } :: { Expr = SubExpression(elseexpr) } :: [] -> 
        let offset, length = exprRepListOffsetLength exprs
        { Offset = offset; Length = length; Expr = IfThenElse (ifexpr, thenexpr, elseexpr) }
    | list -> failwith (sprintf "Incorrect if-then-else syntax: %A" list)

let generateUnitOrSubExpression: ExprRep list -> ExprRep =  
    function
    // Unit
    | [{Offset = offset; Length = length; Expr = SubExpression([]) }] -> { Offset = offset; Length = length; Expr = Unit }
    // Subexpression
    | exprs -> let offset, length = exprRepListOffsetLength exprs
               { Offset = offset; Length = length; Expr = SubExpression exprs } 


let generateNumIterator: ExprRep list -> ExprRep =  
    function
    | startRep :: endRep :: []
      & { Expr = SubExpression(_) } :: { Expr = SubExpression(_) } :: [] -> 
        let length = (endRep.Offset + endRep.Length) - startRep.Offset
        // Report error for the whole generator if the middle is somehow wrong when missing
        let missingVal = { Offset = startRep.Offset; Length = length; Expr = Obj 1L } 
        { Offset = startRep.Offset; Length = length; Expr = Generator (startRep, missingVal, endRep) }
    | startRep :: midRep :: endRep :: [] 
      & { Expr = SubExpression(_) } :: { Expr = SubExpression(_) } :: { Expr = SubExpression(_) } :: [] -> 
        let length = (endRep.Offset + endRep.Length) - startRep.Offset
        { Offset = startRep.Offset; Length = length; Expr = Generator (startRep, midRep, endRep) }
    | list -> failwith (sprintf "Incorrect generator syntax: %A" list)

let generateIndexArgs (exprs: ExprRep list) : ExprRep = 
    let offset, length = exprRepListOffsetLength exprs
    { Offset = offset; Length = length; Expr = IndexArgs <| { Offset = offset; Length = length; Expr = SubExpression exprs } }

let generateBind : ExprRep list -> ExprRep = 
    function
    | exprs & { Offset = offset; Length = length; Expr = SubExpression([{ Expr = Unknown(name) }]) } :: expr :: [] -> 
        { Offset = offset; Length = length; Expr = Binding (name, expr) }
    | list -> failwith (sprintf "Incorrect binding syntax: %A" list)

let allExpressionTypes = 
    [
        { Pattern = [Open; RCap ","; Open];                         Func = generateTuple }
        { Pattern = [Open; SCap "=>"; Open];                        Func = generateLambda }
        { Pattern = [SCap "fun"; SCap "->"; Open];                  Func = generateLambda }
        { Pattern = [SCap "if"; SCap "then"; SCap "else"; Open];    Func = generateIfThenElse }
        { Pattern = [SCap "("; SCap ")"];                           Func = generateUnitOrSubExpression }
        { Pattern = [SCap "{"; RCap ".."; SCap "}"];                Func = generateNumIterator }
        { Pattern = [SCap "["; SCap "]"];                           Func = generateIndexArgs }
        { Pattern = [SCap "let"; SCap "="; SCap "in"];              Func = generateBind }
        { Pattern = [SCap "var"; SCap "="; SCap "in"];              Func = generateBind }
    ]

let allSimpleMappings = 
    [
        ["."], Invoke
        ["()"], Unit
        ["new"], New
        ["null"], Obj null
        ["true"], Obj true
        ["false"], Obj false
        ["=="; "="], Infix (3, objectsEqual)
        ["<>"; "!="], Infix (3, objectsNotEqual)
        [">="], Infix (3, compareObjects (>=))
        ["<="], Infix (3, compareObjects (<=))
        [">"], Infix (3, compareObjects (>))
        ["<"], Infix (3, compareObjects (<))
        ["!"; "not"], Prefix notOp
        ["&"; "&&"; "and"], Infix (4, andOp)
        ["|"; "||"; "or"], Infix (4, orOp)
        ["\\/"], Infix (2, unionObjects)
        ["/\\"], Infix (2, intersectObjects)
        ["/?\\"], Infix (2, doObjectsIntersect)
        ["/"], Infix (1, divideObjects)
        ["*"], Infix (1, multObjects)
        ["+"], Infix (2, addObjects)
        ["-"], Infix (2, subObjects)
    ]

let whitespaceVocabulary = [" "; "\t"; "\r"; "\n"] 

let endUnknownChars = 
    seq {
        for c in whitespaceVocabulary do
            yield c.[0]
        for e in allExpressionTypes do
            for token in e.Pattern do
                match token with
                | Open -> ()
                | SCap (s) 
                | RCap (s) -> yield s.[0]
        for tokens, expr in allSimpleMappings do
            for token in tokens do
                yield token.[0]
    } 
    |> Seq.filter (fun c -> not (c >= 'A' && c <= 'Z') && not (c >= 'a' && c <= 'z'))
    |> Set.ofSeq
    |> Set.toList
    |> List.map (fun c -> string c)    

let (|Skip|_|) (skipStrs: string list) (text: StringWindow) : MatchReturn =
    skipStrs 
    |> List.tryFind (fun sstr -> text.StartsWith(sstr))
    |> Option.map (fun m -> None, text.Subwindow(m.Length))

let (|MapSymbol|_|) (text: StringWindow) : MatchReturn =
    let matches, str = 
        [
            for matchStrs, expr in allSimpleMappings do
                for matchStr in matchStrs do
                    if text.StartsWith(matchStr) then yield matchStr, expr
        ] |> List.allMaxBy (fun (m, expr) -> m.Length)
    match matches with
    | [] -> None
    | [(matched, expr)] -> Some (Some(expr), text.Subwindow(matched.Length))
    | _ -> failwith (sprintf "Ambiguous symbol match: %A" matches)

let (|NewExpression|_|) (typesStack: SubexpressionAndOffset list) (text: StringWindow) =
    let matches, str = 
        [ 
            for ct in allExpressionTypes do 
                match ct.Pattern with
                | (SCap h) :: rest when text.StartsWith(h) -> yield h, { ct with Pattern = rest }
                | (RCap h) :: rest when text.StartsWith(h) -> yield h, ct
                | _ -> ()    
        ] |> List.allMaxBy (fun (m, rest) -> m.Length)
    match matches with
    | [] -> None
    | [(mtext, subexprtype)] -> Some (subexprtype, text.Subwindow(mtext.Length))
    | _ -> failwith (sprintf "Ambiguous expression match: %A" matches)

let (|OngoingExpression|_|) (typesStack: SubexpressionAndOffset list) (text: StringWindow) =
        match typesStack with
        | (current, offset) :: parents -> 
            match current.Pattern with
            | (SCap h) :: rest when text.StartsWith(h) -> Some (h, { current with Pattern = rest })
            | (RCap h) :: rest when text.StartsWith(h) -> Some (h, current)
            | (RCap _) :: (SCap h) :: rest when text.StartsWith(h) -> Some (h, { current with Pattern = rest })
            | _ -> None
            |> Option.map (fun (mtext, expr) -> mtext, (expr, offset) :: parents)       // Add Expression Offset and Parent Subexpressions Back On
            |> Option.map (fun (mtext, subexp) -> subexp, text.Subwindow(mtext.Length)) // Correct Window Offset
        | _ -> None

let (|RefineOpenExpression|_|) (typesStack: SubexpressionAndOffset list) (text: StringWindow) =
    let matches, str = 
        [ 
            for ct in allExpressionTypes do 
                match ct.Pattern with
                | Open :: (SCap h) :: rest when text.StartsWith(h) -> yield h, { ct with Pattern = rest } 
                | Open :: (RCap h) :: rest when text.StartsWith(h) -> yield h, { ct with Pattern = (RCap h) :: rest }
                | _ -> ()
        ] |> List.allMaxBy (fun (m, rest) -> m.Length)   
    match matches with
    | []-> None
    | [(mtext, subexprtype)] -> Some (subexprtype, text.Subwindow(mtext.Length))
    | _ -> failwith (sprintf "Ambiguous open expression match: %A" matches)    

let rec findClosed (typesStack: SubexpressionAndOffset list) = 
    match typesStack with
    | [] -> None
    | ({Pattern = (Open :: _); Func = _}, _) :: rest -> findClosed rest
    | other :: rest -> Some other

// Note, don't move the text pointer when finishing an open expression, so that the parent expression is closed.
let (|FinishOpenExpression|_|) (typesStack: SubexpressionAndOffset list) (text: StringWindow) =
        match typesStack with
        | (current, offset) :: rest -> 
            match current.Pattern with
            | (Open) :: [] 
            | (RCap _) :: Open :: [] -> 
                match findClosed rest with
                | Some (ancestor, ancestorOffset) -> 
                    match ancestor.Pattern with
                    | ((SCap h) :: rest) 
                    | ((RCap h) :: rest) when text.StartsWith(h) -> Some (current, offset, text)
                    | _ -> None
                | None when text.Length = 0 -> Some (current, offset, text)
                | None -> None
            | _ -> None
        | _ -> None

let parseProgram (startText: string) = 
    let rec parseProgramInner (str: StringWindow) (result: ExprRep list) (currentCaptures: (SubexpressionType * int) list) : (StringWindow * ExprRep) =
        try
            match result with
            | { Offset = cSubExprOffset; Expr = SubExpression cSubExpr } :: rSubExprs -> 
                match str with
                | FinishOpenExpression currentCaptures (subtype, expressionStartOffset, crem) ->
                    let length = str.Offset - expressionStartOffset
                    let innerResult = { Offset = expressionStartOffset; Length = length; Expr = SubExpression (cSubExpr |> List.rev) } :: rSubExprs  
                    let value = innerResult |> List.rev |> subtype.Func in 
                        crem, value
                | _ when str.Length = 0 -> 
                    // End of the road, wrap unclosed expressions in a subexpression
                    let cSubExprRev = cSubExpr |> List.rev
                    let newSubExpr = { Offset = str.Offset; Length = str.Offset - cSubExprRev.Head.Offset; Expr = SubExpression cSubExprRev } 
                    str, listToSubExpression (newSubExpr :: rSubExprs)            
                | OngoingExpression currentCaptures (captures, crem) ->
                    match captures with
                    // Expression is Finished
                    | ({ Pattern = []; Func = func }, offset) :: parents -> 
                        let subExprRep = { Offset = offset; Length = crem.Offset - cSubExprOffset; Expr = SubExpression (cSubExpr |> List.rev) }
                        let innerResult = subExprRep :: rSubExprs  
                        let value = innerResult |> List.rev |> func in 
                            crem, value   
                    // Expression Continues
                    | ({ Pattern = h :: rest; Func = _ }, prevOffset) :: parents -> 
                        let fresh = { Offset = str.Offset; Length = Int32.MinValue; Expr = SubExpression [] }
                        let stale = { Offset = prevOffset; Length = str.Offset - prevOffset; Expr = SubExpression (cSubExpr |> List.rev) }
                        parseProgramInner crem (fresh :: stale :: rSubExprs) captures
                    | [] -> failwith "Unexpected output from OngoingExpression"
                | RefineOpenExpression currentCaptures (subtype, crem) ->
                    // Mid-Expression we find evidence that further restricts the kind of expression it is
                    let fresh = { Offset = str.Offset; Length = Int32.MinValue; Expr = SubExpression [] }
                    let stale = { Offset = cSubExprOffset; Length = crem.Offset - cSubExprOffset; Expr = SubExpression (cSubExpr |> List.rev) }
                    let rem, value = parseProgramInner crem (fresh :: stale :: []) ((subtype, str.Offset) :: currentCaptures) 
                    let finalExpr = {value with Expr = SubExpression [value]}
                    //parseProgramInner rem (SubExpression ([value]) :: rSubExprs) currentCaptures
                    parseProgramInner rem (finalExpr :: rSubExprs) currentCaptures    
                | NewExpression currentCaptures (subtype, crem) ->
                    let newExpr = { Offset = str.Offset; Length = Int32.MinValue; Expr = SubExpression [] }   
                    let rem, value = parseProgramInner crem [newExpr] ((subtype, str.Offset) :: currentCaptures)  
                    let finalExpr = { Offset = crem.Offset; Length = rem.Offset - str.Offset; Expr = SubExpression (value :: cSubExpr)}   
                    //parseProgramInner rem (SubExpression (value :: cSubExpr) :: rSubExprs) currentCaptures
                    parseProgramInner rem (finalExpr :: rSubExprs) currentCaptures
                | Skip whitespaceVocabulary res
                | Num res
                | MapSymbol res
                | CaptureString '"' res
                | CaptureString ''' res 
                | CaptureUnknown endUnknownChars res ->
                    let v, rem = res 
                    match v with
                    | Some value -> 
                        let newExpr = { Offset = str.Offset; Length = rem.Offset - str.Offset; Expr = value }
                        let newExprs = { Offset = cSubExprOffset; Length = rem.Offset - cSubExprOffset; Expr = SubExpression (newExpr :: cSubExpr) }
                        parseProgramInner rem (newExprs :: rSubExprs) currentCaptures
                    | None -> parseProgramInner rem result currentCaptures
                | str -> parseProgramInner (str.Subwindow(1)) result currentCaptures
            | _ -> failwith "Expected a SubExpression"
        with ex -> raise <| new BarbParsingException(ex.Message, str.Length)
    let _, res = parseProgramInner (StringWindow(startText, 0)) [{ Offset = 0; Length = 0; Expr = SubExpression [] }] [] in res