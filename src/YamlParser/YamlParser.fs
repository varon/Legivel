﻿module YamlParse

open System
open System.Text
open System.Text.RegularExpressions
open YamlParser.Internals
open YamlParser.Internals.ParserMonads
open TagResolution
open RegexDSL
open RepresentationGraph


exception ParseException of string


type Context = ``Block-out`` | ``Block-in`` | ``Flow-out`` | ``Flow-in`` | ``Block-key`` | ``Flow-key``
type Chomping = ``Strip`` | ``Clip`` | ``Keep``


let ``start-of-line`` = (* RGP "\n" ||| *) RGP "^"
let ``end-of-file`` = RGP "\\z"


let CreateScalarNode tag d = 
    ScalarNode(NodeData<Node>.Create tag d (lazy(NodeHash.Create d)))


let PlainEmptyNode = CreateScalarNode (NonSpecific.NonSpecificTagQM) ""


let CreateMapNode tag d = 
    MapNode(NodeData<(Node*Node) list>.Create tag d 
        (lazy(d 
              |> List.map(fun (k,_) -> k.Hash.Value)
              |> List.sort
              |> NodeHash.Merge)
        )
    )
    


let CreateSeqNode tag d = 
    SeqNode(NodeData<Node list>.Create tag d
        (lazy(d 
              |> List.map(fun e -> e.Hash.Value)
              |> List.sort
              |> NodeHash.Merge)
        )
    )


type Directive = YAML of string | TAG of string*string | RESERVED of string list


type ParseMessage = 
    |   Warn of string
    |   Error of string 


type ParseState = {
        /// Current document line number
        LineNumber  : int
        
        /// String to parse (or what's left of it)
        InputString : string
        
        /// Current Indentation
        n           : int
        
        /// Current Sub-Indentation, nested under ParseState.n
        m           : int
        
        /// Documented context
        c           : Context
        
        /// Chomping type 
        t           : Chomping
        
        /// Document anchors
        Anchors     : Map<string, Node>
        
        /// Parsing messages (warns/errors)
        Messages    : ParseMessage list
        
        /// Document directives
        Directives  : Directive list
        
        /// Tag Shorthands
        TagShorthands : TagShorthand list
        
        /// Global Tag Schema
        GlobalTagSchema : GlobalTagSchema
        
        /// Local Tag Schema
        LocalTagSchema : GlobalTagSchema option

        /// Path from root to current
        NodePath : Node list

        /// (dev only) - Trace of success parses
        TraceSuccess: (string*ParseState) list
    }
    with
        member this.SetRestString s = { this with InputString = s}
        member this.AddAnchor s n = 
//            let a = 
//                if this.Anchors.ContainsKey s then this.Anchors.Remove(s).Add(s, n)
//                else this.Anchors.Remove(s).Add(s, n)
            {this with Anchors = this.Anchors.Add(s, n)}

        member this.AddAnchors al =
            let newAchors = al |> Map.fold(fun (s:Map<string,Node>) k v -> s.Add(k, v)) this.Anchors
            {this with Anchors = newAchors}

        member this.GetAnchor s =
            if this.Anchors.ContainsKey s then this.Anchors.[s]
            else raise (ParseException (sprintf "Referenced anchor '%s' is unknown in line %d" s this.LineNumber))
        
        member this.Advance m = this.SetRestString (Advance(m, this.InputString))

        member this.SkipIfMatch p = this.SetRestString(SkipIfMatch (this.InputString) p)

        member  this.SetStyleContext cn = { this with c = cn}

        member this.SetIndent nn = { this with n = nn}

        member this.SetSubIndent mn = { this with m = mn}

        member this.FullIndented = { this with n = this.n + this.m; m=0 }

        member this.SetChomping tn = { this with t = tn }

        member inline this.OneOf with get() = EitherBuilder(this)

        member this.AddMessage m = {this with Messages = m :: this.Messages}

        member this.Errors   with get() = this.Messages |> List.filter(function | Error _ -> true | _ -> false) |> List.length
        member this.Warnings with get() = this.Messages |> List.filter(function | Warn  _ -> true | _ -> false) |> List.length

        member this.AddDirective d = {this with Directives = d:: this.Directives}

        static member Create inputStr schema = {
                LineNumber = 0; InputString = inputStr ; n=0; m=0; c=``Block-out``; t=``Clip``; 
                Anchors = Map.empty; TraceSuccess = []; Messages=[]; Directives=[]; 
                TagShorthands = [TagShorthand.DefaultSecondaryTagHandler];
                GlobalTagSchema = schema; LocalTagSchema = None; NodePath = []
            }


exception DocumentException of ParseState


[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module ParseState = 
    let inline OneOf (ps:ParseState) = EitherBuilder(ps)
    let inline ``Match and Advance`` (patt:RGXType) postAdvanceFunc (ps:ParseState) =
        match (HasMatches(ps.InputString, patt)) with
        |   (true, _, rest) -> ps.SetRestString rest |> postAdvanceFunc
        |   (false, _, _)    -> None

    let inline ``Match and Parse`` (patt:RGXType) parseFunc (ps:ParseState) =
        match (HasMatches(ps.InputString, patt)) with
        |   (true, mt, rest) -> ps.SetRestString rest |> parseFunc mt
        |   (false, _, _)    -> None

    let SetStyleContext cn (ps:ParseState) = ps.SetStyleContext cn
    let SetIndent nn (ps:ParseState) = ps.SetIndent nn
    let SetSubIndent mn (ps:ParseState) = ps.SetSubIndent mn

    let asc (prs, str, ps) =  { prs with TraceSuccess = (str, ps) :: prs.TraceSuccess }
    let AddSuccessSR str ps pso = pso |> Option.map(fun (any,prs) -> any, asc (prs, str, ps))
    let AddSuccessMR str ps pso = pso |> Option.map(fun (any1,any2,prs) -> any1,any2, asc (prs, str, ps))
    let SetRestString s (ps:ParseState) = ps.SetRestString s

    let renv (prt, ps) =
        prt 
        |> SetStyleContext (ps.c)
        |> SetIndent (ps.n)
        |> SetSubIndent (ps.m)
    let ResetEnvSR ps pso = pso |> Option.map(fun (any, prt) -> (any, (renv(prt,ps))))
    let ResetEnvMR ps pso = pso |> Option.map(fun (any1, any2, prt) -> (any1, any2, (renv(prt,ps))))

    let AddMessage m (ps:ParseState) = ps.AddMessage m
    let AddDirective d (ps:ParseState) = ps.AddDirective d
    let AddAnchorsFrom (prs:ParseState) (ps:ParseState) = ps.AddAnchors prs.Anchors 

let stringPosition (s:string) =
    if s.Length > 10 then s.Substring(0, 10) else s


let restString i o =
    match o with 
    |   None         -> i
    |   Some(c, ors) -> ors.InputString


type ParseFuncSingleResult = (Node * ParseState) option         //  returns parsed node, if possible
type ParseFuncMappedResult = (Node * Node * ParseState) option  //  returns parsed key-value pair, if possible
type ParseFuncSignature = (ParseState -> ParseFuncSingleResult)


type BlockFoldPrevType = EmptyAfterFolded | Empty | Indented | TextLine
type FlowFoldPrevType = Empty | TextLine


type Yaml12Parser(loggingFunction:string->unit) =

    let logger s ps =
        let str = if ps.InputString.Length > 10 then ps.InputString.Substring(0, 10) else ps.InputString
        sprintf "%s\t\"%s\" i:%d c:%A &a:%d" s (str.Replace("\n","\\n")) (ps.n) (ps.c) (ps.Anchors.Count) |> loggingFunction

    new() = Yaml12Parser(fun _ -> ())

    //  Utility functions
    member private this.break sr =
        sr |> Option.map (fun (n,ps) ->
            logger "break" ps
            (n,ps)
        )
        

    member this.``split by linefeed`` s = 
        Regex.Split(s, this.``b-as-line-feed``.ToString()) |> List.ofArray

    member this.``auto detect indent in block`` n (slst: string list) = 
        let ``match indented content`` s = 
            let icp = GRP(ZOM(RGP this.``s-space``)) + OOM(this.``ns-char``)
            match s with
            | Regex2(icp) mt -> Some(mt.ge1.Length - n)
            | _-> None
        slst
        |> List.tryPick(``match indented content``)
        |> function
            | Some v -> v 
            | None   -> 0

    member this.``auto detect indent in line`` ps =
        let icp = GRP(ZOM(RGP this.``s-space``)) + OOM(this.``ns-char``)
        match ps.InputString with
        | Regex2(icp) mt -> (mt.ge1.Length - ps.n)
        | _-> -1 // raise (ParseException(sprintf "Cannot detect indentation at '%s'" (stringPosition ps.InputString)))


    member this.AddAnchor anchor c (ps:ParseState) = if anchor<> "" then (ps.AddAnchor anchor c) else ps

    member this.AddTag tag (c:Node) = if tag<>"" then c.SetTag(Tag.Create(tag)) else c


    member this.``content with properties`` (``follow up func``: ParseFuncSignature) ps =
        match (this.``c-ns-properties`` ps) with
        |   Some(prs, tag, anchor) -> 
            match (HasMatches(prs.InputString, (this.``s-separate`` prs))) with
            |   (true, mt, frs2) -> 
                let prs = prs.SetRestString frs2
                match (prs) with
                |   Parse(``follow up func``) (c, prs) -> 
                    let prs = this.AddAnchor anchor c prs
                    let c = this.AddTag tag c
                    this.ResolveTag prs c |> Some
                |   _ -> None
            |   (false, _, _)    -> Some(this.ResolveTag prs PlainEmptyNode)   //  ``e-scalar``
        |   None    -> None

    member this.``content with optional properties`` (``follow up func``: ParseFuncSignature) ps =
        match (this.``c-ns-properties`` ps) with
        |   Some(prs, tag, anchor) -> 
            match (HasMatches(prs.InputString, (this.``s-separate`` prs))) with
            |   (true, mt, frs2) -> 
                let prs = prs.SetRestString frs2
                match (prs) with
                |   Parse(``follow up func``) (c, prs) -> 
                    let prs = this.AddAnchor anchor c prs
                    let c = this.AddTag tag c
                    this.ResolveTag prs c |> Some
                |   _ -> None
            |   (false, _, _)    -> Some(this.ResolveTag prs PlainEmptyNode)   //  ``e-scalar``
        |   None    -> ``follow up func`` ps

    member this.``block fold lines`` ps (strlst: string list) =
        let ws2 = GRP(ZOM(this.``s-white``)) + GRP(ZOM(this.``ns-char``))
        let rec doFold prev (res:StringBuilder)  (lst: string list) =
            match lst with
            |   []  -> 
                match prev with
                | BlockFoldPrevType.Empty | BlockFoldPrevType.EmptyAfterFolded   -> res.ToString()
                | _ -> if res.ToString().EndsWith("\n") then res.Remove(res.Length-1,1).ToString() else res.ToString()
            |   curr :: tail ->
                match curr with
                |   Regex2(ws2) mt -> 
                    let standardContentAppedAndContinue mode (res:StringBuilder) = doFold mode (res.Append(curr.Substring(ps.n)).Append("\n")) tail
                    logger (sprintf "%s" (res.ToString().Replace("\n","\\n"))) ps
                    let (w, c) = mt.ge2
                    if c = "" then
                        if w.Length > ps.n then standardContentAppedAndContinue Indented res
                        else
                            match prev with
                            | BlockFoldPrevType.Empty | BlockFoldPrevType.Indented | BlockFoldPrevType.EmptyAfterFolded  -> doFold BlockFoldPrevType.Empty (res.Append("\n")) tail
                            | BlockFoldPrevType.TextLine          -> doFold EmptyAfterFolded (res)   tail
                    else
                        if w.Length < ps.n then raise (ParseException(sprintf "Incorrect indentation at: '%s'" curr))
                        else 
                            if w.Length > ps.n then
                                match prev with
                                | BlockFoldPrevType.Empty 
                                | BlockFoldPrevType.Indented 
                                | BlockFoldPrevType.TextLine          -> standardContentAppedAndContinue BlockFoldPrevType.Indented res
                                | BlockFoldPrevType.EmptyAfterFolded  -> standardContentAppedAndContinue Indented (res.Append("\n"))
                            else    //  w.Length = n 
                                match prev with
                                | BlockFoldPrevType.Empty 
                                | BlockFoldPrevType.Indented 
                                | BlockFoldPrevType.EmptyAfterFolded  -> standardContentAppedAndContinue BlockFoldPrevType.TextLine res
                                | BlockFoldPrevType.TextLine          ->
                                    let res = if res.ToString().EndsWith("\n") then res.Remove(res.Length-1,1).Append(" ") else res
                                    doFold BlockFoldPrevType.TextLine (res.Append(curr.Substring(ps.n)).Append("\n") ) tail
                |   _ -> raise (ParseException(sprintf "Incorrect pattern: '%s'" curr))
        let stripAll lst = lst |> List.rev |> List.skipWhile(fun s -> String.IsNullOrWhiteSpace(s)) |> List.rev
        match ps.t with
        | ``Strip`` -> strlst |> stripAll |> doFold BlockFoldPrevType.Empty (new StringBuilder())
        | ``Clip``  -> if (String.IsNullOrWhiteSpace(List.last strlst)) then 
                            List.append (strlst |> stripAll) [""] |> doFold BlockFoldPrevType.Empty (new StringBuilder())
                        else 
                            strlst |> doFold BlockFoldPrevType.Empty (new StringBuilder())
        | ``Keep``  -> strlst |> doFold BlockFoldPrevType.Empty (new StringBuilder())

    member this.``flow fold lines`` (convert:string -> string) ps str =
        let rec doFold fst prev (res : string) (lst: string list) =
            match lst with
            |   []  -> 
                let res = if res.EndsWith("\n") then res.Remove(res.Length-1) else res
                match (prev) with
                |   FlowFoldPrevType.Empty  -> res + " "
                |   FlowFoldPrevType.TextLine -> res.ToString()
            |   curr :: tail ->
                let currStr = 
                    (if fst then curr.TrimEnd() 
                    else if tail.Length = 0 then curr.TrimStart()
                    else curr.Trim()) + "\n"

                let currStr = (convert currStr)
                let currType = 
                    if Regex.IsMatch(currStr, RGS(this.``l-empty`` ps)) then 
                        if fst then FlowFoldPrevType.TextLine else FlowFoldPrevType.Empty
                    else FlowFoldPrevType.TextLine

                logger (sprintf "%s\t\t<-%s" (res.ToString().Replace("\n","\\n")) (currStr.Replace("\n","\\n"))) ps

                match (prev) with
                |   FlowFoldPrevType.Empty  -> doFold false currType (res+currStr) tail
                |   FlowFoldPrevType.TextLine -> 
                    let res = 
                        if res.EndsWith("\n") then
                            match currType with
                            |   FlowFoldPrevType.Empty -> res.Remove(res.Length-1)
                            |   FlowFoldPrevType.TextLine -> res.Remove(res.Length-1) + " "
                        else res
                    doFold false currType (res+currStr) tail
        doFold true FlowFoldPrevType.Empty "" str

    member this.``double quote flowfold lines`` ps str =
        let convertDQuoteEscapedMultiLine (str:string) = 
            let SandR =
                // prevent any collision with other escaped chars
                str.Split([|"\\\\"|], StringSplitOptions.RemoveEmptyEntries) 
                |>  List.ofArray
                |>  List.map(fun s -> s.Replace("\\\n", ""))
                |>  List.map(fun s -> s.Replace("\\n", "\n"))
                |>  List.map(fun s -> s.Replace("\\ ", " "))
                |>  List.map(fun s -> s.Replace("\\t", "\t"))
                |>  List.map(fun s -> s.Replace("\\b", "\b"))
                |>  List.map(fun s -> s.Replace("\\\"", "\""))
                |>  List.map(DecodeEncodedNonAsciiCharacters)
            String.Join("\\", SandR)    // "\\" in dquote is escaped, and here it is directly converted to "\"
        this.``flow fold lines`` convertDQuoteEscapedMultiLine ps str

    member this.``single quote flowfold lines`` ps str =
        let convertSQuoteEscapedMultiLine (str:string) = str.Replace("''", "'")
        this.``flow fold lines`` convertSQuoteEscapedMultiLine ps str

    member this.ResolveTag (ps:ParseState) (node:Node) =
        let isTagInList t l = l |> List.tryFind(fun e -> e.Uri = t.Uri) |> Option.isSome
        let tag = node.NodeTag 
        let isNonSpecificTag = [NonSpecific.NonSpecificTagQM; NonSpecific.NonSpecificTagQT] |> isTagInList tag

        if isNonSpecificTag then
            TagResolutionInfo.Create (tag.Uri) (ps.NodePath) (node) (node.Kind) 
            |> ps.GlobalTagSchema.TagResolution
            |> function
            |   Some t -> (node.SetTag t, ps)
            |   None -> (node, ps |> ParseState.AddMessage (Error (sprintf "Cannot resolve tag: %s" tag.Uri)))
        else
            let isGlobalTag = ps.GlobalTagSchema.GlobalTags |> isTagInList tag
            if isGlobalTag then
                (node, ps)
            else    // extend for local tags
                (node, ps |> ParseState.AddMessage (Error (sprintf "Unrecognized tag: %s" tag.Uri)))

    member this.ResolvedNullNode (ps:ParseState) = PlainEmptyNode |> this.ResolveTag ps |> fst
          
    //  [1] http://www.yaml.org/spec/1.2/spec.html#c-printable
    member this.``c-printable`` = 
        RGO ("\u0009\u000a\u000d\u0020-\u007e" +   // 8 - bit, #x9 | #xA | #xD | [#x20-#x7E]
             "\u0085\u00a0-\ud7ff\ue000-\ufffd")   // 16- bit, #x85 | [#xA0-#xD7FF] | [#xE000-#xFFFD]
                                                   //  32-bit -> currently not supported because .Net does not encode naturally. Yaml: [#x10000-#x10FFFF]

    //  [2] http://www.yaml.org/spec/1.2/spec.html#nb-json
    member ths.``nb-json`` = RGO "\u0009\u0020-\uffff"

    //  [3] http://www.yaml.org/spec/1.2/spec.html#c-byte-order-mark
    member this.``c-byte-order-mark`` = RGP "\ufeff"

    //  [4] http://www.yaml.org/spec/1.2/spec.html#c-sequence-entry
    member this.``c-sequence-entry`` = "-"

    //  [5] http://www.yaml.org/spec/1.2/spec.html#c-mapping-key
    member this.``c-mapping-key`` = "?"

    //  [6] http://www.yaml.org/spec/1.2/spec.html#c-mapping-value
    member this.``c-mapping-value`` = ":"

    //  [7] http://www.yaml.org/spec/1.2/spec.html#c-collect-entry
    member this.``c-collect-entry`` = ","

    //  [8] http://www.yaml.org/spec/1.2/spec.html#c-sequence-start
    member this.``c-sequence-start`` = "["

    //  [9] http://www.yaml.org/spec/1.2/spec.html#c-sequence-end
    member this.``c-sequence-end`` = "]"

    //  [10]    http://www.yaml.org/spec/1.2/spec.html#c-mapping-start
    member this.``c-mapping-start`` = "{"

    //  [11]    http://www.yaml.org/spec/1.2/spec.html#c-mapping-end
    member this.``c-mapping-end`` = "}"

    //  [12]    http://www.yaml.org/spec/1.2/spec.html#c-comment
    member this.``c-comment`` = "#"

    //  [13]    http://www.yaml.org/spec/1.2/spec.html#c-anchor
    member this.``c-anchor`` = "&"

    //  [14]    http://www.yaml.org/spec/1.2/spec.html#c-alias
    member this.``c-alias`` = "*"

    //  [15]    http://www.yaml.org/spec/1.2/spec.html#c-tag
    member this.``c-tag`` = "!"

    //  [16]    http://www.yaml.org/spec/1.2/spec.html#c-literal
    member this.``c-literal`` = "|"

    //  [17]    http://www.yaml.org/spec/1.2/spec.html#c-folded
    member this.``c-folded`` = ">"

    //  [18]    http://www.yaml.org/spec/1.2/spec.html#c-single-quote
    member this.``c-single-quote`` = "\""

    //  [19]    http://www.yaml.org/spec/1.2/spec.html#c-double-quote
    member this.``c-double-quote`` = "\""

    //  [20]    http://www.yaml.org/spec/1.2/spec.html#c-directive
    member this.``c-directive`` = "%"

    //  [21]    http://www.yaml.org/spec/1.2/spec.html#c-reserved
    member this.``c-reserved`` = RGO "\u0040\u0060"

    //  [22]    http://www.yaml.org/spec/1.2/spec.html#c-indicator
    member this.``c-indicator`` = RGO  "\-\?:,\[\]\{\}#&\*!;>\'\"%@`"

    //  [23]    http://www.yaml.org/spec/1.2/spec.html#c-flow-indicator
    member this.``c-flow-indicator`` = RGO  @",\[\]\{\}"

    //  [24]    http://www.yaml.org/spec/1.2/spec.html#b-line-feed
    member this.``b-line-feed`` = RGP "\u000a"

    //  [25]    http://www.yaml.org/spec/1.2/spec.html#b-carriage-return
    member this.``b-carriage-return`` = RGP "\u000d"

    //  [i26]   http://www.yaml.org/spec/1.2/spec.html#b-char
    member this.``b-char`` = this.``b-line-feed`` + this.``b-carriage-return``

    //  [27]    http://www.yaml.org/spec/1.2/spec.html#nb-char
    member this.``nb-char``  = this.``c-printable`` - this.``b-char``

    //  [28]    http://www.yaml.org/spec/1.2/spec.html#b-break
    member this.``b-break`` = 
            (this.``b-carriage-return`` + this.``b-line-feed``) |||  //  DOS, Windows
            this.``b-carriage-return``                          |||  //  MacOS upto 9.x
            this.``b-line-feed``                                     //  UNIX, MacOS X

    //  [29]    http://www.yaml.org/spec/1.2/spec.html#b-as-line-feed
    member this.``b-as-line-feed`` = this.``b-break``

    //  [30]    http://www.yaml.org/spec/1.2/spec.html#b-non-content
    member this.``b-non-content`` = this.``b-break``

    //  [31]    http://www.yaml.org/spec/1.2/spec.html#s-space
    member this.``s-space`` = "\u0020"  // space

    //  [32]    http://www.yaml.org/spec/1.2/spec.html#s-tab
    member this.``s-tab`` = "\u0009"    // tab

    //  [33]    http://www.yaml.org/spec/1.2/spec.html#s-white
    member this.``s-white`` = RGO(this.``s-space`` + this.``s-tab``)

    //  [34]    http://www.yaml.org/spec/1.2/spec.html#ns-char
    member this.``ns-char`` = this.``nb-char`` - this.``s-white``

    //  [35]    http://www.yaml.org/spec/1.2/spec.html#ns-dec-digit
    member this.``ns-dec-digit`` = RGO "\u0030-\u0039"      //  0-9

    //  [36]    http://www.yaml.org/spec/1.2/spec.html#ns-hex-digit
    member this.``ns-hex-digit`` =
        this.``ns-dec-digit`` +
        RGO "\u0041-\u0046"  +  //  A-F
        RGO "\u0061-\u0066"     //  a-f

    //  [37]    http://www.yaml.org/spec/1.2/spec.html#ns-ascii-letter
    member this.``ns-ascii-letter`` = 
        RGO "\u0041-\u005A" +   //  A-Z
        RGO "\u0061-\u007A"     //  a-z

    //  [38]    http://www.yaml.org/spec/1.2/spec.html#ns-word-char
    member this.``ns-word-char`` =
        this.``ns-dec-digit`` + (RGO @"\-") + this.``ns-ascii-letter``

    //  [39]    http://www.yaml.org/spec/1.2/spec.html#ns-uri-char
    member this.``ns-uri-char`` = 
        (RGP @"%") + this.``ns-hex-digit`` + this.``ns-hex-digit``  |||
        (RGO @"#;/?:@&=+$,_.!~*\'\(\)\[\]") + this.``ns-word-char``

    //  [40]    http://www.yaml.org/spec/1.2/spec.html#ns-tag-char
    member this.``ns-tag-char`` = 
        (RGP @"%") + this.``ns-hex-digit`` + this.``ns-hex-digit``  |||
        (RGO @"#;/?:@&=+$_.~*\'\(\)") + this.``ns-word-char``

    //  [41]    http://www.yaml.org/spec/1.2/spec.html#c-escape
    member this.``c-escape`` = RGP "\\"

    //  [42]    http://www.yaml.org/spec/1.2/spec.html#ns-esc-null
    member this.``ns-esc-null`` = RGP "0"

    //  [43]    http://www.yaml.org/spec/1.2/spec.html#ns-esc-bell
    member this.``ns-esc-bell`` = RGP "a"

    //  [44]    http://www.yaml.org/spec/1.2/spec.html#ns-esc-backspace
    member this.``ns-esc-backspace`` = RGP "b"

    //  [45]    http://www.yaml.org/spec/1.2/spec.html#ns-esc-horizontal-tab
    member this.``ns-esc-horizontal-tab`` = RGP "t"

    //  [46]    http://www.yaml.org/spec/1.2/spec.html#ns-esc-line-feed
    member this.``ns-esc-line-feed`` = RGP "n"

    //  [47]    http://www.yaml.org/spec/1.2/spec.html#ns-esc-vertical-tab
    member this.``ns-esc-vertical-tab`` = RGP "v"

    //  [48]    http://www.yaml.org/spec/1.2/spec.html#ns-esc-form-feed
    member this.``ns-esc-form-feed`` = RGP "f"

    //  [49]    http://www.yaml.org/spec/1.2/spec.html#ns-esc-carriage-return
    member this.``ns-esc-carriage-return`` = RGP "r"

    //  [50]    http://www.yaml.org/spec/1.2/spec.html#ns-esc-escape
    member this.``ns-esc-escape`` = RGP "e"

    //  [51]    http://www.yaml.org/spec/1.2/spec.html#ns-esc-space
    member this.``ns-esc-space`` = RGP "\u0020"

    //  [52]    http://www.yaml.org/spec/1.2/spec.html#ns-esc-double-quote
    member this.``ns-esc-double-quote`` = RGP "\""

    //  [53]    http://www.yaml.org/spec/1.2/spec.html#ns-esc-slash
    member this.``ns-esc-slash`` = RGP "/"

    //  [54]    http://www.yaml.org/spec/1.2/spec.html#ns-esc-backslash
    member this.``ns-esc-backslash`` = RGP "\\\\"

    //  [55]    http://www.yaml.org/spec/1.2/spec.html#ns-esc-next-line
    member this.``ns-esc-next-line`` = RGP "N"

    //  [56]    http://www.yaml.org/spec/1.2/spec.html#ns-esc-non-breaking-space
    member this.``ns-esc-non-breaking-space`` = RGP "_"

    //  [57]    http://www.yaml.org/spec/1.2/spec.html#ns-esc-line-separator
    member this.``ns-esc-line-separator`` = RGP "L"

    //  [58]    http://www.yaml.org/spec/1.2/spec.html#ns-esc-paragraph-separator
    member this.``ns-esc-paragraph-separator`` = RGP "P"

    //  [59]    http://www.yaml.org/spec/1.2/spec.html#ns-esc-8-bit
    member this.``ns-esc-8-bit`` = (RGP "x") + Repeat(this.``ns-hex-digit``,2)

    //  [60]    http://www.yaml.org/spec/1.2/spec.html#ns-esc-16-bit
    member this.``ns-esc-16-bit`` = RGP "u" + Repeat(this.``ns-hex-digit``,4)

    //  [61]    http://www.yaml.org/spec/1.2/spec.html#ns-esc-32-bit
    member this.``ns-esc-32-bit`` = RGP "U" + Repeat(this.``ns-hex-digit``,8) // currently not supported

    //  [62]    http://www.yaml.org/spec/1.2/spec.html#c-ns-esc-char
    member this.``c-ns-esc-char`` = 
        RGP ("\\\\") +
            (this.``ns-esc-null``             |||
             this.``ns-esc-bell``             |||
             this.``ns-esc-backspace``        |||
             this.``ns-esc-horizontal-tab``   |||
             this.``ns-esc-line-feed``        |||
             this.``ns-esc-vertical-tab``     |||
             this.``ns-esc-form-feed``        |||
             this.``ns-esc-carriage-return``  |||
             this.``ns-esc-escape``           |||
             this.``ns-esc-space``            |||
             this.``ns-esc-double-quote``     |||
             this.``ns-esc-slash``            |||
             this.``ns-esc-backslash``        |||
             this.``ns-esc-next-line``        |||
             this.``ns-esc-non-breaking-space``|||
             this.``ns-esc-line-separator``   |||
             this.``ns-esc-paragraph-separator``|||
             this.``ns-esc-8-bit``            |||
             this.``ns-esc-16-bit``           |||
             this.``ns-esc-32-bit``)

    //  [63]    http://www.yaml.org/spec/1.2/spec.html#s-indent(n)
    member this.``s-indent(n)`` ps = Repeat(RGP this.``s-space``, ps.n)

    //  [64]    http://www.yaml.org/spec/1.2/spec.html#s-indent(<n)
    member this.``s-indent(<n)`` ps = Range(RGP this.``s-space``, 0, (ps.n-1)) (* Where m < n *)

    //  [65]    http://www.yaml.org/spec/1.2/spec.html#s-indent(≤n)
    member this.``s-indent(<=n)`` ps = Range(RGP this.``s-space``, 0, ps.n)  (* Where m ≤ n *)

    //  [66]    http://www.yaml.org/spec/1.2/spec.html#s-separate-in-line
    member this.``s-separate-in-line`` = OOM(this.``s-white``) ||| ``start-of-line``

    //  [67]    http://www.yaml.org/spec/1.2/spec.html#s-line-prefix(n,c)
    member this.``s-line-prefix`` ps =
        logger "s-line-prefix" ps
        match ps.c with
        | ``Block-out`` ->  this.``s-block-line-prefix`` ps
        | ``Block-in``  ->  this.``s-block-line-prefix`` ps
        | ``Flow-out``  ->  this.``s-flow-line-prefix`` ps
        | ``Flow-in``   ->  this.``s-flow-line-prefix`` ps
        | _             ->  raise(ParseException "The context 'block-key' and 'flow-key' are not supported at this point")

    //  [68]    http://www.yaml.org/spec/1.2/spec.html#s-block-line-prefix(n)
    member this.``s-block-line-prefix`` ps = this.``s-indent(n)`` ps

    //  [69]    http://www.yaml.org/spec/1.2/spec.html#s-flow-line-prefix(n)
    member this.``s-flow-line-prefix`` ps = (this.``s-indent(n)`` ps) + OPT(this.``s-separate-in-line``)

    //  [70]    http://www.yaml.org/spec/1.2/spec.html#l-empty(n,c)
    member this.``l-empty`` ps = ((this.``s-line-prefix`` ps) ||| (this.``s-indent(<n)`` ps)) + this.``b-as-line-feed``

    //  [71]    http://www.yaml.org/spec/1.2/spec.html#b-l-trimmed(n,c)
    member this.``b-l-trimmed`` ps = this.``b-non-content`` + OOM(this.``l-empty`` ps)

    //  [72]    http://www.yaml.org/spec/1.2/spec.html#b-as-space
    member this.``b-as-space`` = this.``b-break``

    //  [73]    http://www.yaml.org/spec/1.2/spec.html#b-l-folded(n,c)
    member this.``b-l-folded`` ps = (this.``b-l-trimmed`` ps) ||| this.``b-as-space``

    //  [74]    http://www.yaml.org/spec/1.2/spec.html#s-flow-folded(n)
    member this.``s-flow-folded`` (ps:ParseState) =
        OPT(this.``s-separate-in-line``) + (this.``b-l-folded`` (ps.SetStyleContext ``Flow-in``)) + (this.``s-flow-line-prefix`` ps)

    //  [75]    http://www.yaml.org/spec/1.2/spec.html#c-nb-comment-text
    member this.``c-nb-comment-text`` = RGP("#") + ZOM(this.``nb-char``)

    //  [76]    http://www.yaml.org/spec/1.2/spec.html#b-comment
    member this.``b-comment`` = this.``b-non-content`` ||| RGP("\\z") // EOF..

    //  [77]    http://www.yaml.org/spec/1.2/spec.html#s-b-comment
    member this.``s-b-comment`` = OPT(this.``s-separate-in-line`` + OPT(this.``c-nb-comment-text``)) + this.``b-comment`` 

    //  [78]    http://www.yaml.org/spec/1.2/spec.html#l-comment
    member this.``l-comment`` = this.``s-separate-in-line`` + OPT(this.``c-nb-comment-text``) + this.``b-comment``

    //  [79]    http://www.yaml.org/spec/1.2/spec.html#s-l-comments
    member this.``s-l-comments`` = (this.``s-b-comment`` ||| ``start-of-line``) + ZOM(this.``l-comment``)

    //  [80]    http://www.yaml.org/spec/1.2/spec.html#s-separate(n,c)
    member this.``s-separate`` ps = 
        logger "s-separate" ps
        match ps.c with 
        | ``Block-out`` ->  this.``s-separate-lines`` ps
        | ``Block-in``  ->  this.``s-separate-lines`` ps
        | ``Flow-out``  ->  this.``s-separate-lines`` ps
        | ``Flow-in``   ->  this.``s-separate-lines`` ps
        | ``Block-key`` ->  this.``s-separate-in-line``
        | ``Flow-key``  ->  this.``s-separate-in-line``

    //  [81]    http://www.yaml.org/spec/1.2/spec.html#s-separate-lines(n)
    member this.``s-separate-lines`` ps = (this.``s-l-comments`` + (this.``s-flow-line-prefix`` ps)) ||| this.``s-separate-in-line``

    //  [82]    http://www.yaml.org/spec/1.2/spec.html#l-directive
    member this.``l-directive`` (ps:ParseState) : (Directive * ParseState) option = 
        ps 
        |> ParseState.``Match and Advance`` (RGP "%") (fun prs ->
            match prs.InputString with
            |   Regex2(this.``ns-yaml-directive``)  mt    -> 
                let ps = 
                    match (mt.ge1.Split('.') |> List.ofArray) with
                    | [a;b] when a="1" && b<"2" -> ps |> ParseState.AddMessage (Warn (sprintf "YAML %s document will be parsed as YAML 1.2" mt.ge1))
                    | [a;b] when a="1" && b="2" -> ps
                    | [a;b] when a="1" && b>"2" -> ps |> ParseState.AddMessage (Warn (sprintf "YAML %s document will be parsed as YAML 1.2" mt.ge1))
                    | [a;_] when a>"1"          -> ps |> ParseState.AddMessage (Error (sprintf "YAML %s document cannot be parsed, only YAML 1.2 is supported" mt.ge1))
                    | _                         -> ps |> ParseState.AddMessage (Error (sprintf "Illegal directive: %%YAML %s, document cannot be parsed" mt.ge1))
                let ymlcnt = ps.Directives |> List.filter(function | YAML _ -> true | _ -> false) |> List.length
                let ps = 
                    if ymlcnt>0 then (ps |> ParseState.AddMessage (Error "The YAML directive must only be given at most once per document."))
                    else ps
                if ps.Errors >0 then
                    raise (DocumentException ps)
                else
                    Some(YAML(mt.ge1), ps.SetRestString mt.Rest)
            |   Regex2(this.``ns-tag-directive``)   mt    -> Some(TAG(mt.ge2),  ps.SetRestString mt.Rest)
            |   Regex2(this.``ns-reserved-directive``) mt -> Some(RESERVED(mt.Groups), ps |> ParseState.SetRestString mt.Rest |> ParseState.AddMessage (Warn (sprintf "Unsupported directive: %%%s" mt.ge1)))
            |   _   -> None
        )
        |> Option.bind(fun (t,prs) ->
            prs |> ParseState.``Match and Advance`` (this.``s-l-comments``) (fun prs2 -> Some(t,prs2))
        )

    //  [83]    http://www.yaml.org/spec/1.2/spec.html#ns-reserved-directive
    member this.``ns-reserved-directive`` = 
        this.``ns-directive-name`` + ZOM(this.``s-separate-in-line`` + this.``ns-directive-parameter``)

    //  [84]    http://www.yaml.org/spec/1.2/spec.html#ns-directive-name
    member this.``ns-directive-name`` = OOM(this.``ns-char``)

    //  [85]    http://www.yaml.org/spec/1.2/spec.html#ns-directive-parameter
    member this.``ns-directive-parameter`` = OOM(this.``ns-char``)

    //  [86]    http://www.yaml.org/spec/1.2/spec.html#ns-yaml-directive
    member this.``ns-yaml-directive`` = RGP("YAML") + this.``s-separate-in-line`` + GRP(this.``ns-yaml-version``)

    //  [87]    http://www.yaml.org/spec/1.2/spec.html#ns-yaml-version
    member this.``ns-yaml-version`` = OOM(this.``ns-dec-digit``) + RGP("\\.") + OOM(this.``ns-dec-digit``)

    //  [88]    http://www.yaml.org/spec/1.2/spec.html#ns-tag-directive
    member this.``ns-tag-directive`` = 
        (RGP "TAG") + this.``s-separate-in-line`` + GRP(this.``c-tag-handle``) + this.``s-separate-in-line`` + GRP(this.``ns-tag-prefix``)

    //  [89]    http://www.yaml.org/spec/1.2/spec.html#c-tag-handle
    member this.``c-tag-handle`` = this.``c-named-tag-handle`` ||| this.``c-secondary-tag-handle`` ||| this.``c-primary-tag-handle``

    //  [90]    http://www.yaml.org/spec/1.2/spec.html#c-primary-tag-handle
    member this.``c-primary-tag-handle`` = RGP "!"

    //  [91]    http://www.yaml.org/spec/1.2/spec.html#c-secondary-tag-handle
    member this.``c-secondary-tag-handle`` = RGP "!!"

    //  [92]    http://www.yaml.org/spec/1.2/spec.html#c-named-tag-handle
    member this.``c-named-tag-handle`` = (RGP "!") + OOM(this.``ns-word-char``) + (RGP "!") 

    //  [93]    http://www.yaml.org/spec/1.2/spec.html#ns-tag-prefix
    member this.``ns-tag-prefix`` = this.``c-ns-local-tag-prefix`` ||| this.``ns-global-tag-prefix``

    //  [94]    http://www.yaml.org/spec/1.2/spec.html#c-ns-local-tag-prefix
    member this.``c-ns-local-tag-prefix`` = (RGP "!") + ZOM(this.``ns-uri-char``)

    //  [95]    http://www.yaml.org/spec/1.2/spec.html#ns-global-tag-prefix
    member this.``ns-global-tag-prefix`` = this.``ns-tag-char`` + ZOM(this.``ns-uri-char``)

    //  [96]    http://www.yaml.org/spec/1.2/spec.html#c-ns-properties(n,c)
    member this.``c-ns-properties`` ps : (ParseState * string * string) option =
        logger "c-ns-properties" ps
        let ``tag anchor`` = GRP(this.``c-ns-tag-property``) + OPT((this.``s-separate`` ps) + GRP(this.``c-ns-anchor-property``))
        let ``anchor tag`` = GRP(this.``c-ns-anchor-property``) + OPT((this.``s-separate`` ps) + GRP(this.``c-ns-tag-property``))
        match (ps.InputString) with
        |   Regex2(``tag anchor``) mt -> 
            let (tag, anchor) = mt.ge2
            let prs = ps.SetRestString (SkipIfMatch (ps.InputString) ``tag anchor``)
            Some(prs, tag, anchor.Substring(1))
        |   Regex2(``anchor tag``) mt -> 
            let (anchor, tag) = mt.ge2
            let prs = ps.SetRestString (SkipIfMatch (ps.InputString) ``anchor tag``)
            Some(prs, tag, anchor.Substring(1))
        |   _ -> None

    //  [97]    http://www.yaml.org/spec/1.2/spec.html#c-ns-tag-property
    member this.``c-ns-tag-property`` = this.``c-verbatim-tag`` ||| this.``c-ns-shorthand-tag`` ||| this.``c-non-specific-tag``

    //  [98]    http://www.yaml.org/spec/1.2/spec.html#c-verbatim-tag
    member this.``c-verbatim-tag`` = (RGP "!<") + OOM(this.``ns-uri-char``) + (RGP ">") 

    //  [99]    http://www.yaml.org/spec/1.2/spec.html#c-ns-shorthand-tag
    member this.``c-ns-shorthand-tag`` = this.``c-tag-handle`` + OOM(this.``ns-tag-char``)

    //  [100]   http://www.yaml.org/spec/1.2/spec.html#c-non-specific-tag
    member this.``c-non-specific-tag`` = RGP "!"

    //  [101]   http://www.yaml.org/spec/1.2/spec.html#c-ns-anchor-property
    member this.``c-ns-anchor-property`` = (RGP "&") + this.``ns-anchor-name``

    //  [102]   http://www.yaml.org/spec/1.2/spec.html#ns-anchor-char
    member this.``ns-anchor-char`` =  this.``ns-char`` - this.``c-flow-indicator``

    //  [103]   http://www.yaml.org/spec/1.2/spec.html#ns-anchor-name
    member this.``ns-anchor-name`` = OOM(this.``ns-anchor-char``)

    //  [104]   http://www.yaml.org/spec/1.2/spec.html#c-ns-alias-node
    member this.``c-ns-alias-node`` ps : ParseFuncSingleResult =
        logger "c-ns-alias-node" ps
        ps |> ParseState.``Match and Advance`` (RGP "\\*") (fun prs ->
            prs |> ParseState.``Match and Parse`` (this.``ns-anchor-name``) (fun mt prs2 ->
                Some((ps.GetAnchor mt), prs2)
        ))
        |> ParseState.AddSuccessSR "c-ns-alias-node" ps

    //  [105]   http://www.yaml.org/spec/1.2/spec.html#e-scalar
    member this.``e-scalar`` = RGP String.Empty     // we'll see if this works..

    //  [106]   http://www.yaml.org/spec/1.2/spec.html#e-node
    member this.``e-node`` = this.``e-scalar``

    //  [107]   http://www.yaml.org/spec/1.2/spec.html#nb-double-char
    member this.``nb-double-char`` = this.``c-ns-esc-char`` ||| (this.``nb-json`` - RGO("\\\\\""))

    //  [108]   http://www.yaml.org/spec/1.2/spec.html#ns-double-char
    member this.``ns-double-char`` = this.``c-ns-esc-char`` |||  (this.``nb-json`` - RGO("\\\\\"") - this.``s-white``)

    //  [109]   http://www.yaml.org/spec/1.2/spec.html#c-double-quoted(n,c)
    member this.``c-double-quoted`` ps : ParseFuncSingleResult = 
        logger "c-double-quoted" ps
        let convertDQuoteEscapedSingleLine (str:string) = 
            let SandR =
                // prevent any collision with other escaped chars
                str.Split([|"\\\\"|], StringSplitOptions.RemoveEmptyEntries) 
                |>  List.ofArray
                |>  List.map(fun s -> s.Replace("\\\"", "\""))
            String.Join("\\", SandR)    // "\\" in dquote is escaped, and here it is directly converted to "\"

        let patt = RGS((RGP "\"") + GRP(this.``nb-double-text`` ps) + (RGP "\""))
        match ps.InputString with 
        |   Regex(patt)  [full; content] ->
            let n = 
                match ps.c with
                |  ``Flow-out`` |  ``Flow-in`` ->   //  multiline
                    content 
                    |> this.``split by linefeed``
                    |> this.``double quote flowfold lines`` ps
                    |> CreateScalarNode (NonSpecific.NonSpecificTagQT)
                    |> this.ResolveTag ps
                    |> fst
                |   ``Block-key`` | ``Flow-key`` -> //  single line
                    content 
                    |> convertDQuoteEscapedSingleLine
                    |> CreateScalarNode (NonSpecific.NonSpecificTagQT)
                    |> this.ResolveTag ps
                    |> fst
                | _             ->  raise(ParseException "The context 'block-out' and 'block-in' are not supported at this point")
            let prs = ps.Advance full
            Some(n, prs)
        |   _ -> None
        |> ParseState.AddSuccessSR "c-double-quoted" ps        

    //  [110]   http://www.yaml.org/spec/1.2/spec.html#nb-double-text(n,c)
    member this.``nb-double-text`` ps =
        match ps.c with
        | ``Flow-out``  ->  this.``nb-double-multi-line`` ps
        | ``Flow-in``   ->  this.``nb-double-multi-line`` ps
        | ``Block-key`` ->  this.``nb-double-one-line``
        | ``Flow-key``  ->  this.``nb-double-one-line``
        | _             ->  raise(ParseException "The context 'block-out' and 'block-in' are not supported at this point")

    //  [111]   http://www.yaml.org/spec/1.2/spec.html#nb-double-one-line
    member this.``nb-double-one-line`` = ZOM(this.``nb-double-char``)

    //  [112]   http://www.yaml.org/spec/1.2/spec.html#s-double-escaped(n)
    member this.``s-double-escaped`` (ps:ParseState) = ZOM(this.``s-white``) + (RGP "\\\\") + this.``b-non-content`` + ZOM(this.``l-empty`` (ps.SetStyleContext ``Flow-in``)) + (this.``s-flow-line-prefix`` ps)

    //  [113]   http://www.yaml.org/spec/1.2/spec.html#s-double-break(n)
    member this.``s-double-break`` ps = (this.``s-double-escaped`` ps) ||| (this.``s-flow-folded`` ps)

    //  [114]   http://www.yaml.org/spec/1.2/spec.html#nb-ns-double-in-line
    member this.``nb-ns-double-in-line`` = ZOM(ZOM(this.``s-white``) + this.``ns-double-char``)

    //  [115]   http://www.yaml.org/spec/1.2/spec.html#s-double-next-line(n)
    member this.``s-double-next-line`` ps =  //  note, spec is recursive, below is an attempt to rewrite recursive regex 
        ZOM((this.``s-double-break`` ps) + this.``ns-double-char`` + this.``nb-ns-double-in-line``) + (this.``s-double-break`` ps) |||
        OOM((this.``s-double-break`` ps) + this.``ns-double-char`` + this.``nb-ns-double-in-line``) + ZOM(this.``s-white``)

    //  [116]   http://www.yaml.org/spec/1.2/spec.html#nb-double-multi-line(n)
    member this.``nb-double-multi-line`` ps = this.``nb-ns-double-in-line`` + ((this.``s-double-next-line`` ps) ||| ZOM(this.``s-white``))

    //  [117]    http://www.yaml.org/spec/1.2/spec.html#c-quoted-quote
    member this.``c-quoted-quote`` = RGP "\'\'"

    //  [118]   http://www.yaml.org/spec/1.2/spec.html#nb-single-char
    member this.``nb-single-char`` = this.``c-quoted-quote`` ||| (this.``nb-json`` - (RGP "\'"))

    //  [119]   http://www.yaml.org/spec/1.2/spec.html#ns-single-char
    member this.``ns-single-char`` = // this.``nb-single-char`` - this.``s-white``
        this.``c-quoted-quote`` ||| (this.``nb-json`` - (RGO "\'") - this.``s-white``)

    //  [120]   http://www.yaml.org/spec/1.2/spec.html#c-single-quoted(n,c)
    member this.``c-single-quoted`` ps : ParseFuncSingleResult = 
        logger "c-single-quoted" ps
        let convertSQuoteEscapedSingleLine (str:string) = str.Replace("''", "'")
        let patt = RGS((RGP "'") + GRP(this.``nb-single-text`` ps) + (RGP "'"))
        match ps.InputString with
        |   Regex(patt)  [full; content] ->
            let n = 
                match ps.c with
                |  ``Flow-out`` |  ``Flow-in`` ->   //  multiline
                    content 
                    |> this.``split by linefeed``
                    |> this.``single quote flowfold lines`` ps
                    |> CreateScalarNode (NonSpecific.NonSpecificTagQT)
                    |> this.ResolveTag ps |> fst
                |   ``Block-key`` | ``Flow-key`` -> //  single line
                    content 
                    |> convertSQuoteEscapedSingleLine
                    |> CreateScalarNode (NonSpecific.NonSpecificTagQT)
                    |> this.ResolveTag ps |> fst
                | _             ->  raise(ParseException "The context 'block-out' and 'block-in' are not supported at this point")
            let prs = ps.Advance full
            Some(n, prs)
        |   _ -> None
        |> ParseState.AddSuccessSR "c-single-quoted" ps        

    //  [121]   http://www.yaml.org/spec/1.2/spec.html#nb-single-text(n,c)
    member this.``nb-single-text`` ps =
        logger "nb-single-text" ps
        match ps.c with
        |   ``Flow-out``    -> this.``nb-single-multi-line`` ps
        |   ``Flow-in``     -> this.``nb-single-multi-line`` ps
        |   ``Block-key``   -> this.``nb-single-one-line``
        |   ``Flow-key``    -> this.``nb-single-one-line``
        | _             ->  raise(ParseException "The context 'block-out' and 'block-in' are not supported at this point")

    //  [122]   http://www.yaml.org/spec/1.2/spec.html#nb-single-one-line    
    member this.``nb-single-one-line`` = ZOM(this.``nb-single-char``)

    //  [123]   http://www.yaml.org/spec/1.2/spec.html#nb-ns-single-in-line
    member this.``nb-ns-single-in-line`` = ZOM(ZOM(this.``s-white``) + this.``ns-single-char``)

    //  [124]   http://www.yaml.org/spec/1.2/spec.html#s-single-next-line(n)
    member this.``s-single-next-line`` ps = //  note, spec is recursive, below is an attempt to rewrite recursive regex 
        ZOM((this.``s-flow-folded`` ps) + this.``ns-single-char`` + this.``nb-ns-single-in-line``) + (this.``s-flow-folded`` ps) |||
        OOM((this.``s-flow-folded`` ps) + this.``ns-single-char`` + this.``nb-ns-single-in-line``) + ZOM(this.``s-white``)        

    //  [125]   http://www.yaml.org/spec/1.2/spec.html#nb-single-multi-line(n)
    member this.``nb-single-multi-line`` ps = this.``nb-ns-single-in-line`` + ((this.``s-single-next-line`` ps) ||| ZOM(this.``s-white``))

    //  [126]   http://www.yaml.org/spec/1.2/spec.html#ns-plain-first(c)
    member this.``ns-plain-first`` ps = (this.``ns-char`` - this.``c-indicator``) ||| ((RGP "\\?") ||| (RGP ":") ||| (RGP "-")) + (this.``ns-plain-safe`` ps)

    //  [127]   http://www.yaml.org/spec/1.2/spec.html#ns-plain-safe(c)
    member this.``ns-plain-safe`` ps =
        logger "ns-plain-safe" ps
        match ps.c with
        |   ``Flow-out``    -> this.``ns-plain-safe-out``
        |   ``Flow-in``     -> this.``ns-plain-safe-in``
        |   ``Block-key``   -> this.``ns-plain-safe-out``
        |   ``Flow-key``    -> this.``ns-plain-safe-in``
        | _             ->  raise(ParseException "The context 'block-out' and 'block-in' are not supported at this point")

    //  [128]   http://www.yaml.org/spec/1.2/spec.html#ns-plain-safe-out
    member this.``ns-plain-safe-out`` = this.``ns-char``

    //  [129]   http://www.yaml.org/spec/1.2/spec.html#ns-plain-safe-in
    member this.``ns-plain-safe-in`` = this.``ns-char`` - this.``c-flow-indicator``

    //  [130]   http://www.yaml.org/spec/1.2/spec.html#ns-plain-char(c)
    member this.``ns-plain-char`` ps = (this.``ns-plain-safe`` ps) - (RGO ":#") ||| (this.``ns-char`` + (RGP "#")) ||| (RGP ":") + (this.``ns-plain-safe`` ps)

    //  [131]   http://www.yaml.org/spec/1.2/spec.html#ns-plain(n,c)
    member this.``ns-plain`` (ps:ParseState) =
        logger "ns-plain" ps
        match ps.c with
        | ``Flow-out``  -> this.``ns-plain-multi-line`` ps
        | ``Flow-in``   -> this.``ns-plain-multi-line`` ps
        | ``Block-key`` -> this.``ns-plain-one-line`` ps
        | ``Flow-key``  -> this.``ns-plain-one-line`` ps
        | _              -> raise(ParseException "The context 'block-out' and 'block-in' are not supported at this point")

    //  [132]   http://www.yaml.org/spec/1.2/spec.html#nb-ns-plain-in-line(c)
    member this.``nb-ns-plain-in-line`` ps = ZOM(ZOM(this.``s-white``) + (this.``ns-plain-char`` ps))

    //  [133]   http://www.yaml.org/spec/1.2/spec.html#ns-plain-one-line(c)
    member this.``ns-plain-one-line`` ps = (this.``ns-plain-first`` ps) + (this.``nb-ns-plain-in-line`` ps)

    //  [134]   http://www.yaml.org/spec/1.2/spec.html#s-ns-plain-next-line(n,c)
    member this.``s-ns-plain-next-line`` ps = (this.``s-flow-folded`` ps) + (this.``ns-plain-char`` ps) + (this.``nb-ns-plain-in-line`` ps)

    //  [135]   http://www.yaml.org/spec/1.2/spec.html#ns-plain-multi-line(n,c)
    member this.``ns-plain-multi-line`` ps = (this.``ns-plain-one-line`` ps) + ZOM(this.``s-ns-plain-next-line`` ps)

    //  [136]   http://www.yaml.org/spec/1.2/spec.html#in-flow(c)
    member this.``in-flow`` (ps:ParseState) =
        match ps.c with
        |   ``Flow-out`` -> ``Flow-in``
        |   ``Flow-in``  -> ``Flow-in``
        |   ``Block-key``-> ``Flow-key``
        |   ``Flow-key`` -> ``Flow-key``
        | _              -> raise(ParseException "The context 'block-out' and 'block-in' are not supported at this point")

    //  [137]   http://www.yaml.org/spec/1.2/spec.html#c-flow-sequence(n,c)
    member this.``c-flow-sequence`` (ps:ParseState) : ParseFuncSingleResult=
        logger "c-flow-sequence" ps
        ps |> ParseState.``Match and Advance`` ((RGP "\\[") + OPT(this.``s-separate`` ps)) (fun prs ->
            match (prs.SetStyleContext(this.``in-flow`` prs)) with
            |   Parse(this.``ns-s-flow-seq-entries``) (c, prs2) ->  
                match (HasMatches(prs2.InputString, (RGP "\\]"))) with
                |  (true, mt, frs)  -> Some(c, prs2.SetRestString frs)
                |  (false, _,_)    -> None
            |   _ ->
                match (HasMatches(prs.InputString, (RGP "\\]"))) with
                |  (true, mt, frs) ->  CreateSeqNode (NonSpecific.NonSpecificTagQT) [] |> this.ResolveTag (ps.SetRestString frs) |> Some
                |  (false, _,_)    -> None
        )
        |> ParseState.ResetEnvSR ps
        |> ParseState.AddSuccessSR "c-flow-sequence" ps        
                
    //  [138]   http://www.yaml.org/spec/1.2/spec.html#ns-s-flow-seq-entries(n,c)
    member this.``ns-s-flow-seq-entries`` (ps:ParseState) : ParseFuncSingleResult =
        logger "ns-s-flow-seq-entries" ps
        let rec ``ns-s-flow-seq-entries`` (ps:ParseState) (lst:Node list) : ParseFuncSingleResult =
            match ps with
            |   Parse(this.``ns-flow-seq-entry``) (entry, prs) ->
                let lst = entry :: lst
                let rs = SkipIfMatch (prs.InputString) (OPT(this.``s-separate`` prs))
                match (HasMatches(rs, (RGP ",") + OPT(this.``s-separate`` prs))) with 
                |   (true, mt, frs) -> ``ns-s-flow-seq-entries`` (prs.SetRestString frs) lst
                |   (false, _,_)    -> CreateSeqNode (NonSpecific.NonSpecificTagQT) (lst |> List.rev) |> this.ResolveTag (prs.SetRestString rs) |> Some
            |   _ -> 
                if lst.Length = 0 then None   // empty sequence
                else CreateSeqNode (NonSpecific.NonSpecificTagQT) (lst |> List.rev) |> this.ResolveTag ps |> Some
        ``ns-s-flow-seq-entries`` ps []
        |> ParseState.AddSuccessSR "ns-s-flow-seq-entries" ps        

    //  [139]   http://www.yaml.org/spec/1.2/spec.html#ns-flow-seq-entry(n,c)
    member this.``ns-flow-seq-entry`` (ps:ParseState) : ParseFuncSingleResult =
        logger "ns-flow-seq-entry" ps
        match ps with
        |   Parse(this.``ns-flow-pair``) (ck, cv, prs) -> 
            CreateMapNode (NonSpecific.NonSpecificTagQT) [(ck,cv)] |> this.ResolveTag prs |> Some
        |   Parse(this.``ns-flow-node``) (c, prs) -> Some(c, prs)
        |   _ -> None
        |> ParseState.AddSuccessSR "ns-flow-seq-entry"  ps

    //  [140]   http://www.yaml.org/spec/1.2/spec.html#c-flow-mapping(n,c)
    member this.``c-flow-mapping`` (ps:ParseState) : ParseFuncSingleResult =
        logger "c-flow-mapping" ps
        match (HasMatches(ps.InputString, RGS((RGP "\\{") + OPT(this.``s-separate`` ps)))) with
        |   (false, _, _)    -> None
        |   (true, mt, frs) -> 
            let prs = ps.SetRestString frs
            match (prs.SetStyleContext(this.``in-flow`` prs)) with
            |   Parse(this.``ns-s-flow-map-entries``) (c, prs2) -> 
                match (HasMatches(prs2.InputString, (RGP "\\}"))) with
                |   (true, mt, frs2) -> Some(c, prs2.SetRestString frs2)
                |   (false, _, _)    -> raise (ParseException(sprintf "Expected '}' at \"%s\"" (prs2.InputString.Substring(6))))
            |   _ -> 
                match (HasMatches(prs.InputString, (RGP "\\}"))) with
                |   (true, mt, frs2) -> CreateMapNode (NonSpecific.NonSpecificTagQT) [] |> this.ResolveTag (ps.SetRestString frs2) |> Some
                |   (false, _, _)    -> None
        |> ParseState.ResetEnvSR ps
        |> ParseState.AddSuccessSR "c-flow-mapping" ps       
            
    //  [141]   http://www.yaml.org/spec/1.2/spec.html#ns-s-flow-map-entries(n,c)
    member this.``ns-s-flow-map-entries`` (ps:ParseState) : ParseFuncSingleResult =
        logger "ns-s-flow-map-entries" ps
        let rec ``ns-s-flow-map-entries`` (ps:ParseState) (lst:(Node*Node) list) : ParseFuncSingleResult =
            match (ps) with
            |   Parse(this.``ns-flow-map-entry``) (ck, cv, prs) ->
                let lst = (ck, cv) :: lst
                let prs = prs.SkipIfMatch (OPT(this.``s-separate`` prs))
                match (HasMatches(prs.InputString, (RGP ",") + OPT(this.``s-separate`` prs))) with 
                |   (true, mt, frs) ->  
                    ``ns-s-flow-map-entries`` (prs.SetRestString frs) lst
                |   (false, _,_)    -> CreateMapNode (NonSpecific.NonSpecificTagQT) (lst |> List.rev) |> this.ResolveTag prs |> Some
            |   _ -> None   // empty sequence
        ``ns-s-flow-map-entries`` ps []
        |> ParseState.AddSuccessSR "ns-s-flow-map-entries" ps        

    //  [142]   http://www.yaml.org/spec/1.2/spec.html#ns-flow-map-entry(n,c)
    member this.``ns-flow-map-entry`` (ps:ParseState) : ParseFuncMappedResult =
        logger "ns-flow-map-entry" ps
        let ``ns-flow-map-explicit-entry`` ps = 
            match (HasMatches(ps.InputString, RGP "\\?" + (this.``s-separate`` ps))) with
            | (false, _, _) ->  None
            | (true, m, inputrs)  -> this.``ns-flow-map-explicit-entry`` (ps.SetRestString inputrs)
        (ps |> ParseState.OneOf) {
            either (``ns-flow-map-explicit-entry``)
            either (this.``ns-flow-map-implicit-entry``)
            ifneither (None)        
        }
        |> ParseState.AddSuccessMR "ns-flow-map-entry" ps        

    //  [143]   http://www.yaml.org/spec/1.2/spec.html#ns-flow-map-explicit-entry(n,c)
    member this.``ns-flow-map-explicit-entry`` (ps:ParseState) : ParseFuncMappedResult =
        logger "ns-flow-map-explicit-entry" ps
        match (ps) with
        |   Parse(this.``ns-flow-map-implicit-entry``) (ck, cv, prs) -> Some(ck, cv, prs)
        |   _ -> Some(this.ResolvedNullNode ps, this.ResolvedNullNode ps, ps)       // ( ``e-node`` + ``e-node``)
        |> ParseState.AddSuccessMR "ns-flow-map-explicit-entry" ps       

    //  [144]   http://www.yaml.org/spec/1.2/spec.html#ns-flow-map-implicit-entry(n,c)
    member this.``ns-flow-map-implicit-entry`` (ps:ParseState) : ParseFuncMappedResult =
        logger "ns-flow-map-implicit-entry" ps
        (ps |> ParseState.OneOf) {
            either (this.``ns-flow-map-yaml-key-entry``)
            either (this.``c-ns-flow-map-empty-key-entry``)
            either (this.``c-ns-flow-map-json-key-entry``)
            ifneither (None)        
        }
        |> ParseState.AddSuccessMR "ns-flow-map-implicit-entry" ps        

    //  [145]   http://www.yaml.org/spec/1.2/spec.html#ns-flow-map-yaml-key-entry(n,c)
    member this.``ns-flow-map-yaml-key-entry`` (ps:ParseState) =
        logger "ns-flow-map-yaml-key-entry" ps
        match (ps) with
        |   Parse(this.``ns-flow-yaml-node``) (ck, prs) -> 
            let prs = prs.SkipIfMatch (OPT(this.``s-separate`` prs))
            match (prs) with
            |   Parse(this.``c-ns-flow-map-separate-value``) (cv, prs2) -> Some(ck,cv, prs2)
            |   _ -> Some(ck, this.ResolvedNullNode prs, prs)  //  ``e-node``
        |   _   -> None
        |> ParseState.AddSuccessMR "ns-flow-map-yaml-key-entry" ps     

    //  [146]   http://www.yaml.org/spec/1.2/spec.html#c-ns-flow-map-empty-key-entry(n,c)
    member this.``c-ns-flow-map-empty-key-entry`` (ps:ParseState) : ParseFuncMappedResult =
        logger "c-ns-flow-map-empty-key-entry" ps
        match (ps) with
        |   Parse(this.``c-ns-flow-map-separate-value``) (c, prs) -> Some(this.ResolvedNullNode prs, c, prs)   //  ``e-node``
        |   _ -> None
        |> ParseState.AddSuccessMR "c-ns-flow-map-empty-key-entry" ps     

    //  [147]   http://www.yaml.org/spec/1.2/spec.html#c-ns-flow-map-separate-value(n,c)
    member this.``c-ns-flow-map-separate-value`` (ps:ParseState) : ParseFuncSingleResult =
        logger "c-ns-flow-map-separate-value" ps
        match (HasMatches(ps.InputString, (RGP ":") + NOT(this.``ns-plain-safe`` ps) )) with
        |   (true, mt, frs) -> 
            match (HasMatches(frs, (this.``s-separate`` ps))) with
            |   (true, mt, frs2) -> 
                let prs = ps.SetRestString frs2
                match (prs) with
                |   Parse(this.``ns-flow-node``) (c, rs) -> Some(c, rs)
                |   _ -> None
            |   (false, _, _)    -> PlainEmptyNode |> this.ResolveTag (ps.SetRestString frs) |> Some   //  ``e-node``
        |   (false, _, _)    -> None
        |> ParseState.AddSuccessSR "c-ns-flow-map-separate-value" ps     

    //  [148]   http://www.yaml.org/spec/1.2/spec.html#c-ns-flow-map-json-key-entry(n,c)
    member this.``c-ns-flow-map-json-key-entry`` (ps:ParseState) =
        logger "c-ns-flow-map-json-key-entry" ps
        match (ps) with
        |   Parse(this.``c-flow-json-node``) (ck, prs) -> 
            let prs = prs.SkipIfMatch (OPT(this.``s-separate`` prs))
            match (prs) with
            |   Parse(this.``c-ns-flow-map-adjacent-value``) (cv, prs2) -> Some(ck,cv, prs2)
            |   _ -> Some(ck, this.ResolvedNullNode prs, prs)  //  ``e-node``
        |   _    -> None
        |> ParseState.AddSuccessMR "c-ns-flow-map-json-key-entry" ps

    //  [149]   http://www.yaml.org/spec/1.2/spec.html#c-ns-flow-map-adjacent-value(n,c)
    member this.``c-ns-flow-map-adjacent-value`` (ps:ParseState) =
        logger "c-ns-flow-map-adjacent-value" ps
        match (HasMatches(ps.InputString, (RGP ":"))) with
        |   (true, mt, frs) -> 
            let prs = ps.SetRestString frs
            let prs = prs.SkipIfMatch (OPT(this.``s-separate`` ps))
            match (prs) with
            |   Parse(this.``ns-flow-node``) (c, prs2) -> Some(c, prs2)
            |   _ -> Some(this.ResolvedNullNode prs, prs)  //  ``e-node``
        |   (false, _, _)    -> None
        |> ParseState.AddSuccessSR "c-ns-flow-map-adjacent-value" ps

    //  [150]   http://www.yaml.org/spec/1.2/spec.html#ns-flow-pair(n,c)
    member this.``ns-flow-pair`` (ps:ParseState) : ParseFuncMappedResult =
        logger "ns-flow-pair" ps
        let ``ns-flow-map-explicit-entry`` (ps:ParseState) = 
            match (HasMatches(ps.InputString, RGP "\\?" + (this.``s-separate`` ps))) with
            | (false, _, _) ->  None
            | (true, m, inputrs)  -> this.``ns-flow-map-explicit-entry`` (ps.SetRestString inputrs)
        (ps |> ParseState.OneOf) {
            either (``ns-flow-map-explicit-entry``)
            either (this.``ns-flow-pair-entry``)
            ifneither (None)        
        }
        |> ParseState.AddSuccessMR "ns-flow-pair" ps

    //  [151]   http://www.yaml.org/spec/1.2/spec.html#ns-flow-pair-entry(n,c)
    member this.``ns-flow-pair-entry`` (ps:ParseState) : ParseFuncMappedResult =
        logger "ns-flow-pair-entry" ps
        (ps |> ParseState.OneOf) {
            either (this.``ns-flow-pair-yaml-key-entry``)
            either (this.``c-ns-flow-map-empty-key-entry``)
            either (this.``c-ns-flow-pair-json-key-entry``)
            ifneither (None)        
        }
        |> ParseState.AddSuccessMR "ns-flow-pair-entry" ps

    //  [152]   http://www.yaml.org/spec/1.2/spec.html#ns-flow-pair-yaml-key-entry(n,c)
    member this.``ns-flow-pair-yaml-key-entry`` (ps:ParseState) : ParseFuncMappedResult =
        logger "ns-flow-pair-yaml-key-entry" ps
        let ``ns-s-implicit-yaml-key`` (ps:ParseState) = (this.``ns-s-implicit-yaml-key`` (ps.SetStyleContext ``Flow-key``))
        match (ps) with
        |   Parse(``ns-s-implicit-yaml-key``) (ck, prs) -> 
            match (prs) with
            |   Parse(this.``c-ns-flow-map-separate-value``) (cv, prs2) -> Some(ck, cv, prs2)
            |   _ ->    None
        |   _ -> None
        |> ParseState.ResetEnvMR ps
        |> ParseState.AddSuccessMR "ns-flow-pair-yaml-key-entry" ps

    //  [153]   http://www.yaml.org/spec/1.2/spec.html#c-ns-flow-pair-json-key-entry(n,c)
    member this.``c-ns-flow-pair-json-key-entry`` (ps:ParseState) : ParseFuncMappedResult =
        logger "c-ns-flow-pair-json-key-entry" ps
        match (this.``c-s-implicit-json-key`` (ps.SetStyleContext ``Flow-key``)) with
        |   Some(ck, prs) ->
            match (this.``c-ns-flow-map-adjacent-value`` prs) with
            |   Some(cv, prs2) -> Some(ck, cv, prs2)
            |   None -> None
        |   None -> None
        |> ParseState.ResetEnvMR ps
        |> ParseState.AddSuccessMR "c-ns-flow-pair-json-key-entry" ps

    //  [154]   http://www.yaml.org/spec/1.2/spec.html#ns-s-implicit-yaml-key(c)
    member this.``ns-s-implicit-yaml-key`` (ps:ParseState) : ParseFuncSingleResult =
        logger "ns-s-implicit-yaml-key" ps
//        let ``n/a`` = 0
//        let ps = ps.SetIndent ``n/a``
        match (ps) with
        |   Parse(this.``ns-flow-yaml-node``) (ck, prs) -> 
            let prs = prs.SetRestString(SkipIfMatch prs.InputString (OPT(this.``s-separate-in-line``)))
            Some(ck, prs)
        |   _ -> None
        |> ParseState.AddSuccessSR "ns-s-implicit-yaml-key" ps

    //  [155]   http://www.yaml.org/spec/1.2/spec.html#c-s-implicit-json-key(c)
    member this.``c-s-implicit-json-key`` (ps:ParseState) : ParseFuncSingleResult = (* At most 1024 characters altogether *)
        logger "c-s-implicit-json-key" ps
//        let ``n/a`` = 0
//        let ps = ps.SetIndent ``n/a``
        match (ps) with
        |   Parse(this.``c-flow-json-node``)    (c, prs) -> 
            let prs = prs.SkipIfMatch (OPT(this.``s-separate-in-line``))
            Some(c, prs)
        |   _   -> None
        |> ParseState.AddSuccessSR  "c-s-implicit-json-key" ps

    //  [156]   http://www.yaml.org/spec/1.2/spec.html#ns-flow-yaml-content(n,c)
    member this.``ns-flow-yaml-content`` (ps:ParseState) : ParseFuncSingleResult = 
        logger "ns-flow-yaml-content" ps
        match (HasMatches(ps.InputString, RGS(this.``ns-plain`` ps))) with
        |   (true, mt, frs) -> CreateScalarNode (NonSpecific.NonSpecificTagQM) (mt) |> this.ResolveTag (ps.SetRestString frs) |> Some
        |   (false, _, _)    -> None
        |> ParseState.AddSuccessSR  "ns-flow-yaml-content" ps

    //  [157]   http://www.yaml.org/spec/1.2/spec.html#c-flow-json-content(n,c)
    member this.``c-flow-json-content`` (ps:ParseState) : ParseFuncSingleResult =
        logger "c-flow-json-content" ps
        ps.OneOf {
            either (this.``c-single-quoted``)
            either (this.``c-double-quoted``)
            either (this.``c-flow-mapping``)
            either (this.``c-flow-sequence``)
            ifneither (None)
        }
        |> ParseState.AddSuccessSR  "c-flow-json-content" ps
        
    //  [158]   http://www.yaml.org/spec/1.2/spec.html#ns-flow-content(n,c)
    member this.``ns-flow-content`` (ps:ParseState) : ParseFuncSingleResult =
        logger "ns-flow-content" ps
        ps.OneOf {
            either (this.``ns-flow-yaml-content``)
            either (this.``c-flow-json-content``)
            ifneither (None)
        }
        |> ParseState.AddSuccessSR  "ns-flow-content" ps

    //  [159]   http://www.yaml.org/spec/1.2/spec.html#ns-flow-yaml-node(n,c)
    member this.``ns-flow-yaml-node`` (ps:ParseState) : ParseFuncSingleResult =
        logger "ns-flow-yaml-node" ps
        ps.OneOf {
            either (this.``c-ns-alias-node``)
            either (this.``ns-flow-yaml-content``)
            either (this.``content with properties`` this.``ns-flow-yaml-content``)
            ifneither (None)
        }
        |> ParseState.AddSuccessSR  "ns-flow-yaml-node" ps

    //  [160]   http://www.yaml.org/spec/1.2/spec.html#c-flow-json-node(n,c)
    member this.``c-flow-json-node`` (ps:ParseState) : ParseFuncSingleResult =
        logger "c-flow-json-node" ps
        ps.OneOf {
            either (this.``content with optional properties`` this.``c-flow-json-content``)
            ifneither (None)
        }
        |> ParseState.AddSuccessSR "c-flow-json-node" ps
    
    //  [161]   http://www.yaml.org/spec/1.2/spec.html#ns-flow-node(n,c)
    member this.``ns-flow-node`` (ps:ParseState) : ParseFuncSingleResult =
        logger "ns-flow-node" ps
        ps.OneOf {
            either (this.``c-ns-alias-node``)
            either (this.``ns-flow-content``)
            either (this.``content with properties`` this.``ns-flow-content``)
            ifneither (None)
        }
        |> ParseState.AddSuccessSR "ns-flow-node" ps

    //  [162]   http://www.yaml.org/spec/1.2/spec.html#c-b-block-header(m,t)
    member this.``c-b-block-header`` (ps:ParseState) = 
        logger "c-b-block-header" ps
        let chomp indicator =
            match indicator with
            |   "-" -> ``Strip``
            |   "+" -> ``Keep``
            |   ""  -> ``Clip``
            |   _ -> raise (ParseException(sprintf "Illegal chomp indicator '%s'" indicator))
        let indent i = 
            if i = "" then None    
            else Some(Int32.Parse i)

        let ``indent chomp`` ps : (int option * ParseState) option = 
            let p = GRP(this.``c-indentation-indicator``) + GRP(this.``c-chomping-indicator``) + this.``s-b-comment``
            match ps.InputString with
            | Regex2(p)  mt -> 
                let (i, c) = mt.ge2
                Some(indent  i, (ps.SetRestString mt.Rest).SetChomping (chomp c))
            |   _ -> None
        let ``chomp indent`` ps : (int option * ParseState) option = 
            let p = GRP(this.``c-chomping-indicator``) + GRP(this.``c-indentation-indicator``) + this.``s-b-comment``
            match ps.InputString with
            | Regex2(p)  mt -> 
                let (c, i) = mt.ge2
                Some(indent  i, (ps.SetRestString mt.Rest).SetChomping (chomp c))
            |   _ -> None
        match (ps) with
        |   Eval(``indent chomp``)  value -> value
        |   Eval(``chomp indent``)  value -> value
        |   _ ->    (None, ps.SetChomping ``Clip``)

    //  [163]   http://www.yaml.org/spec/1.2/spec.html#c-indentation-indicator(m)
    member this.``c-indentation-indicator`` = OPT(this.``ns-dec-digit``)

    //  [164]   http://www.yaml.org/spec/1.2/spec.html#c-chomping-indicator(t)
    member this.``c-chomping-indicator`` = OPT(RGP("\\+") ||| RGP("-"))

    //  [165]   http://www.yaml.org/spec/1.2/spec.html#b-chomped-last(t)
    member this.``b-chomped-last`` ps =
        logger "b-chomped-last" ps
        match ps.t with
        |   ``Strip``   -> this.``b-non-content``    ||| RGP("\\z")
        |   ``Clip``    -> this.``b-as-line-feed``   ||| RGP("\\z")
        |   ``Keep``    -> this.``b-as-line-feed``   ||| RGP("\\z")

    //  [166]   http://www.yaml.org/spec/1.2/spec.html#l-chomped-empty(n,t)
    member this.``l-chomped-empty`` (ps:ParseState) =
        logger "l-chomped-empty" ps
        match ps.t with
        |   ``Strip``   -> this.``l-strip-empty`` ps
        |   ``Clip``    -> this.``l-strip-empty`` ps
        |   ``Keep``    -> this.``l-keep-empty`` ps

    //  [167]   http://www.yaml.org/spec/1.2/spec.html#l-strip-empty(n)
    member this.``l-strip-empty`` ps = ZOM((this.``s-indent(<=n)`` ps) + this.``b-non-content``) + OPT(this.``l-trail-comments`` ps)

    //  [168]   http://www.yaml.org/spec/1.2/spec.html#l-keep-empty(n)
    member this.``l-keep-empty`` ps = ZOM(this.``l-empty`` (ps.SetStyleContext ``Block-in``)) + OPT(this.``l-trail-comments`` ps)

    //  [169]   http://www.yaml.org/spec/1.2/spec.html#l-trail-comments(n)
    member this.``l-trail-comments`` ps = (this.``s-indent(<n)`` ps) + this.``c-nb-comment-text`` + this.``b-comment``

    //  [170]   http://www.yaml.org/spec/1.2/spec.html#c-l+literal(n)
    member this.``c-l+literal`` ps = 
        logger "c-l+literal" ps
        let trimIndent n (slst: string list) =
            let rec processlLine n l frst (src: string list) dst =
                let join sl = String.Join("\n", sl |> List.rev)
                let cont v =
                    if src.Length > 0 then processlLine n (src.Head) false (src.Tail) (v :: dst)
                    else join (v :: dst)
                let ws2 = GRP(ZOM(RGP this.``s-space``)) + GRP(ZOM(this.``nb-char`` - RGO(this.``s-space``)))
                match l with
                |   Regex2(ws2) mt -> 
                    let (w, c) = mt.ge2
                    if frst then
                        if w.Length > n then 
                            raise (ParseException "A leading all-space line must not have too many spaces")
                        else
                            if c = "" then cont ""
                            else cont (l.Substring(n))
                    else
                        if w.Length < n then 
                            if w = "" && c = "" then cont ""
                            else raise (ParseException "A following text line must not be less indented.")
                        else cont (l.Substring(n))
                | _ -> raise (ParseException (sprintf "Unexpected characters, at %s" l))
            
            processlLine n (slst.Head) true (slst.Tail) []

        match (HasMatches(ps.InputString, RGP "\\|")) with
        | (false, _, _)   ->  None
        | (true, mt, frs) ->
            let ``literal-content`` (ps:ParseState) =
                let ps = if ps.n < 0 then (ps.SetIndent 0) else ps
                let p = this.``l-literal-content`` ps
                match ps.InputString  with
                |   Regex2(p)  m -> Some(m.ge1, ps.SetRestString m.Rest)
                |   _ -> None

            let prs = ps.SetRestString frs
            let (pm, prs2) = (this.``c-b-block-header`` prs)

            let m = match pm with
                    |   Some(m) -> m
                    |   None    ->
                    match (``literal-content`` prs2) with
                    |   Some(ms, prs3) ->  
                        let split = ms |> this.``split by linefeed`` 
                        let aut = split |> this.``auto detect indent in block`` prs2.n
                        if aut < 0 then raise (ParseException "Lesser indented than expected")
                        aut
                    |   None  -> raise (ParseException "Could not detect indentation of literal block scalar after '|'")

            match (``literal-content`` (prs2.SetIndent (prs2.n+m))) with
            |   Some(ms, ps2) ->  
                let split = ms |> this.``split by linefeed`` 
                let s = split |> trimIndent (ps2.n)
                Some(s, ps2)
            |   None  -> None
        |> ParseState.AddSuccessSR "c-l+literal" ps

    //  [171]   http://www.yaml.org/spec/1.2/spec.html#l-nb-literal-text(n)
    member this.``l-nb-literal-text`` (ps:ParseState) = ZOM(this.``l-empty`` (ps.SetStyleContext ``Block-in``)) + (this.``s-indent(n)`` ps) + OOM(this.``nb-char``)

    //  [172]   http://www.yaml.org/spec/1.2/spec.html#b-nb-literal-next(n)
    member this.``b-nb-literal-next`` ps = this.``b-as-line-feed`` + (this.``l-nb-literal-text`` ps)
    
    //  [173]   http://www.yaml.org/spec/1.2/spec.html#l-literal-content(n,t)
    member this.``l-literal-content`` (ps:ParseState) = 
        GRP(OPT((this.``l-nb-literal-text`` ps) + ZOM(this.``b-nb-literal-next`` ps) + (this.``b-chomped-last`` ps))) + (this.``l-chomped-empty`` ps)

    //  [174]   http://www.yaml.org/spec/1.2/spec.html#c-l+folded(n)
    member this.``c-l+folded`` ps =
        logger "c-l+folded" ps
        match (HasMatches(ps.InputString, RGP ">")) with
        | (false, _, _)   ->  None
        | (true, mt, frs) ->
            let ``folded-content`` (ps:ParseState) =
                let ps = if ps.n < 0 then ps.SetIndent 0 else ps
                let p = this.``l-folded-content`` (ps.FullIndented)
                match ps.InputString  with
                |   Regex2(p)  m -> Some(m.ge1, ps.SetRestString m.Rest)
                |   _ -> None
            
            let prs = ps.SetRestString frs
            let (pm, prs2) = (this.``c-b-block-header`` prs)
            
            let m = match pm with
                    |   Some(m) -> m
                    |   None    ->
                    match (``folded-content`` prs2) with
                    |   Some(ms, prs3) ->  
                        let split = ms |> this.``split by linefeed`` 
                        let aut = split |> this.``auto detect indent in block`` prs2.n
                        if aut < 0 then raise (ParseException "Lesser indented than expected")
                        aut
                    |   None  -> raise (ParseException "Could not detect indentation of literal block scalar after '>'")

            match (``folded-content`` (prs2.SetIndent (prs2.n+m))) with
            |   Some(ms, ps2) ->  
                let s = ms |> this.``split by linefeed`` |> this.``block fold lines`` (ps2)
                Some(s, ps2)
            |   None  -> None
        |> ParseState.ResetEnvSR ps
        |> ParseState.AddSuccessSR "c-l+folded" ps 

    //  [175]   http://www.yaml.org/spec/1.2/spec.html#s-nb-folded-text(n)
    member this.``s-nb-folded-text`` ps = (this.``s-indent(n)`` ps) + ZOM(this.``nb-char``)

    //  [176]   http://www.yaml.org/spec/1.2/spec.html#l-nb-folded-lines(n)
    member this.``l-nb-folded-lines`` (ps:ParseState) = (this.``s-nb-folded-text`` ps) + ZOM((this.``b-l-folded`` (ps.SetStyleContext ``Block-in``)) + this.``s-nb-folded-text`` ps)

    //  [177]   http://www.yaml.org/spec/1.2/spec.html#s-nb-spaced-text(n)
    member this.``s-nb-spaced-text`` ps = (this.``s-indent(n)`` ps) + this.``s-white`` + ZOM(this.``nb-char``)

    //  [178]   http://www.yaml.org/spec/1.2/spec.html#b-l-spaced(n)
    member this.``b-l-spaced`` (ps:ParseState) = this.``b-as-line-feed`` + ZOM(this.``l-empty`` (ps.SetStyleContext ``Block-in``))

    //  [179]   http://www.yaml.org/spec/1.2/spec.html#l-nb-spaced-lines(n)
    member this.``l-nb-spaced-lines`` ps = (this.``s-nb-spaced-text`` ps) + ZOM((this.``b-l-spaced``ps) + (this.``s-nb-spaced-text`` ps))

    //  [180]   http://www.yaml.org/spec/1.2/spec.html#l-nb-same-lines(n)
    member this.``l-nb-same-lines`` (ps:ParseState) = 
        ZOM(this.``l-empty`` (ps.SetStyleContext ``Block-in``)) + ((this.``l-nb-folded-lines`` ps) ||| (this.``l-nb-spaced-lines`` ps))

    //  [181]   http://www.yaml.org/spec/1.2/spec.html#l-nb-diff-lines(n)
    member this.``l-nb-diff-lines`` ps = (this.``l-nb-same-lines`` ps) + ZOM(this.``b-as-line-feed`` + (this.``l-nb-same-lines`` ps))

    //  [182]   http://www.yaml.org/spec/1.2/spec.html#l-folded-content(n,t)
    member this.``l-folded-content`` (ps:ParseState) =
        GRP(OPT((this.``l-nb-diff-lines`` ps) + (this.``b-chomped-last`` ps))) + (this.``l-chomped-empty`` ps)

    //  [183]   http://www.yaml.org/spec/1.2/spec.html#l+block-sequence(n)
    member this.``l+block-sequence`` (ps:ParseState) = 
        logger "l+block-sequence" ps
        let m = this.``auto detect indent in line`` ps
        if m < 1 then None // raise (ParseException "Incorrect indentation")
        else
            let ps = ps.SetSubIndent m
            let rec ``l+block-sequence`` ps (acc: Node list) =
                let contentOrNone = 
                    if (acc.Length = 0) then None
                    else CreateSeqNode (NonSpecific.NonSpecificTagQT) (List.rev acc) |> this.ResolveTag ps |> Some
                match HasMatches(ps.InputString, RGS((this.``s-indent(n)`` (ps.FullIndented)))) with
                |   (true, mt, frs) -> 
                    let ps = ps.SetRestString frs
                    match (this.``c-l-block-seq-entry`` ps.FullIndented) with
                    |   Some(c, prs2) -> ``l+block-sequence`` prs2 (c :: acc)
                    |   _ -> contentOrNone
                |   (false, _, _) -> contentOrNone 
            ``l+block-sequence`` ps []
        |> ParseState.ResetEnvSR ps
        |> ParseState.AddSuccessSR "l+block-sequence" ps

    //  [184]   http://www.yaml.org/spec/1.2/spec.html#c-l-block-seq-entry(n)
    member this.``c-l-block-seq-entry`` ps =
        logger "c-l-block-seq-entry" ps
        match (HasMatches(ps.InputString, RGP("-"))) with
        |   (true, mt, frs) -> 
            if IsMatch(frs, (this.``ns-char``)) then // Not followed by an ns-char
                None
            else
                let prs = ps.SetRestString frs
                let prs = prs.SetStyleContext ``Block-in``
                this.``s-l+block-indented`` prs
        |   (false, _, _) -> None
        |> ParseState.ResetEnvSR ps
        |> ParseState.AddSuccessSR "c-l-block-seq-entry" ps

    //  [185]   http://www.yaml.org/spec/1.2/spec.html#s-l+block-indented(n,c)
    member this.``s-l+block-indented`` ps =
        logger "s-l+block-indented" ps

        let m = (ps.n) + (this.``auto detect indent in line`` ps)
        let m = if m<0 then 0 else m
        let ps = ps.SetSubIndent m

        match HasMatches(ps.InputString, RGS((this.``s-indent(n)`` (ps.SetIndent ps.m)))) with
        |   (true, mt, frs) -> 
            let prs = ps |> ParseState.SetRestString frs |> ParseState.SetIndent (ps.n+1+ps.m) |> ParseState.SetSubIndent 0
            (prs |> ParseState.OneOf) {
                either (this.``ns-l-compact-sequence``)
                either (this.``ns-l-compact-mapping``)
                either (fun _ -> this.``s-l+block-node`` prs)
                ifneither (
                    let prs2 = prs.SkipIfMatch (this.``e-node`` + this.``s-l-comments``)
                    Some(this.ResolvedNullNode prs2, prs2))
            }
        |   (false, _, _) -> None
        |> ParseState.ResetEnvSR ps
        |> ParseState.AddSuccessSR "s-l+block-indented" ps

    //  [186]   http://www.yaml.org/spec/1.2/spec.html#ns-l-compact-sequence(n)
    member this.``ns-l-compact-sequence`` ps = 
        logger "ns-l-compact-sequence" ps
        let rec ``ns-l-compact-sequence`` ps (acc: Node list) =
            let contentOrNone = 
                if (acc.Length = 0) then None
                else CreateSeqNode (NonSpecific.NonSpecificTagQT) (List.rev acc) |> this.ResolveTag ps |> Some
            match HasMatches(ps.InputString, RGS((this.``s-indent(n)`` ps))) with
            |   (true, mt, frs) -> 
                let prs = ps.SetRestString frs
                match (this.``c-l-block-seq-entry`` prs) with
                |   Some(c, prs2) -> ``ns-l-compact-sequence`` prs2 (c :: acc)
                |   _ -> contentOrNone
            |   (false, _, _) -> contentOrNone
        match ps with
        |   Parse(this.``c-l-block-seq-entry``) (c, prs) -> ``ns-l-compact-sequence`` prs [c]
        |   _ ->    None
        |> ParseState.AddSuccessSR "ns-l-compact-sequence" ps

    //  [187]   http://www.yaml.org/spec/1.2/spec.html#l+block-mapping(n)
    member this.``l+block-mapping`` ps =
        logger "l+block-mapping" ps
        let m = this.``auto detect indent in line`` ps
        if m < 1 then None // raise (ParseException "Incorrect indentation")
        else
            let ps = ps.SetSubIndent m
            let rec ``l+block-mapping`` ps (acc:(Node*Node) list) = 
                let contentOrNone = 
                    if (acc.Length = 0) then None
                    else CreateMapNode (NonSpecific.NonSpecificTagQT) (List.rev acc) |> this.ResolveTag ps |> Some
                match (HasMatches(ps.InputString, RGS((this.``s-indent(n)`` (ps.FullIndented))))) with
                |   (true, mt, frs) -> 
                    let ps = ps.SetRestString frs
                    match (this.``ns-l-block-map-entry`` (ps.FullIndented)) with
                    |   Some(ck, cv, prs)    ->  ``l+block-mapping`` prs ((ck,cv) :: acc)
                    |   None            ->  contentOrNone
                |   (false, _, _) -> contentOrNone
            ``l+block-mapping`` ps []
        |> ParseState.ResetEnvSR ps
        |> ParseState.AddSuccessSR "l+block-mapping" ps

    //  [188]   http://www.yaml.org/spec/1.2/spec.html#ns-l-block-map-entry(n)
    member this.``ns-l-block-map-entry`` (ps:ParseState) : ParseFuncMappedResult = 
        logger "ns-l-block-map-entry" ps
        (ps |> ParseState.OneOf) {
            either (this.``c-l-block-map-explicit-entry``)
            either (this.``ns-l-block-map-implicit-entry``)
            ifneither (None)
        }
        |> ParseState.AddSuccessMR "ns-l-block-map-entry" ps

    //  [189]   http://www.yaml.org/spec/1.2/spec.html#c-l-block-map-explicit-entry(n)
    member this.``c-l-block-map-explicit-entry`` (ps:ParseState) : ParseFuncMappedResult=
        logger "c-l-block-map-explicit-entry" ps
        match (this.``c-l-block-map-explicit-key`` ps) with
        |   Some(ck, prs1) ->
            match (prs1) with
            |   Parse(this.``l-block-map-explicit-value``) (cv, prs1) -> Some(ck, cv, prs1)
            |   _   ->
                match HasMatches(prs1.InputString, RGS(this.``e-node``)) with
                |   (true, mt, frs) -> 
                    let prs2 = prs1.SetRestString frs
                    Some(ck, this.ResolvedNullNode prs2, prs2)
                |   _ -> raise (ParseException "Cannot identify mapping value")
        |   None -> None
        |> ParseState.AddSuccessMR "c-l-block-map-explicit-entry" ps

    //  [190]   http://www.yaml.org/spec/1.2/spec.html#c-l-block-map-explicit-key(n)
    member this.``c-l-block-map-explicit-key`` ps : ParseFuncSingleResult =
        logger "c-l-block-map-explicit-key" ps
        match HasMatches(ps.InputString, RGP("\\?")) with
        |   (true, mt, frs) -> 
            let prs = ps.SetRestString frs
            let prs = prs.SetStyleContext ``Block-out``
            this.``s-l+block-indented`` prs
        |   (false, _, _) -> None
        |> ParseState.ResetEnvSR ps
        |> ParseState.AddSuccessSR "c-l-block-map-explicit-key" ps

    //  [191]   http://www.yaml.org/spec/1.2/spec.html#l-block-map-explicit-value(n)
    member this.``l-block-map-explicit-value`` ps = 
        logger "l-block-map-explicit-value" ps
        match HasMatches(ps.InputString, RGS((this.``s-indent(n)`` ps) + RGP(":"))) with
        |   (true, mt, frs) -> 
            let prs = ps.SetRestString frs
            let prs = prs.SetStyleContext ``Block-out``
            this.``s-l+block-indented`` prs
        |   (false, _, _) -> None
        |> ParseState.ResetEnvSR ps
        |> ParseState.AddSuccessSR "l-block-map-explicit-value" ps

    //  [192]   http://www.yaml.org/spec/1.2/spec.html#ns-l-block-map-implicit-entry(n)
    member this.``ns-l-block-map-implicit-entry`` ps : ParseFuncMappedResult =
        logger "ns-l-block-map-implicit-entry" ps
        let matchValue (ck, prs) =
            match prs with
            |   Parse(this.``c-l-block-map-implicit-value``) (cv, prs2) -> Some(ck, cv, prs2)
            |   _ ->  None //  raise (ParseException "Expecting an implicit mapping value")
        match (ps) with
        |   Parse(this.``ns-s-block-map-implicit-key``) (ck, prs1) -> matchValue(ck, prs1)
        |   _   ->
            match HasMatches(ps.InputString, RGS(this.``e-node``)) with
            |   (true, mt, frs) -> matchValue (this.ResolveTag (ps.SetRestString frs) PlainEmptyNode)
            |   _ -> None
        |> ParseState.AddSuccessMR "ns-l-block-map-implicit-entry" ps

    //  [193]   http://www.yaml.org/spec/1.2/spec.html#ns-s-block-map-implicit-key
    member this.``ns-s-block-map-implicit-key`` ps = 
        logger "ns-s-block-map-implicit-key" ps
        (ps |> ParseState.SetStyleContext ``Block-key`` |> ParseState.OneOf) {
            either (this.``c-s-implicit-json-key``)
            either (this.``ns-s-implicit-yaml-key``)
            ifneither (None)
        }
        |> ParseState.ResetEnvSR ps
        |> ParseState.AddSuccessSR "ns-s-block-map-implicit-key" ps

    //  [194]   http://www.yaml.org/spec/1.2/spec.html#c-l-block-map-implicit-value(n)
    member this.``c-l-block-map-implicit-value`` (ps:ParseState) : ParseFuncSingleResult=
        logger "c-l-block-map-implicit-value" ps
        match (HasMatches(ps.InputString, RGS(RGP(":")))) with
        |   (true, mt, frs) -> 
            let prs = ps.SetRestString frs
            let prs = prs.SetStyleContext ``Block-out``
            match (prs) with
            |   Parse(this.``s-l+block-node``)  (c, prs2) -> Some(c, prs2)
            |   _ ->
                match (HasMatches(prs.InputString, RGS((this.``e-node`` +  this.``s-l-comments``)))) with
                |   (true, mt, frs2) -> Some(this.ResolveTag (prs.SetRestString frs2) PlainEmptyNode)
                |   (false, _, _) -> None
        |   (false, _, _) -> None
        |> ParseState.ResetEnvSR ps
        |> ParseState.AddSuccessSR "c-l-block-map-implicit-value" ps

    //  [195]   http://www.yaml.org/spec/1.2/spec.html#ns-l-compact-mapping(n)
    member this.``ns-l-compact-mapping`` ps =
        logger "ns-l-compact-mapping" ps
        let rec ``ns-l-compact-mapping`` ps (acc: (Node * Node) list) =
            let contentOrNone = 
                if (acc.Length = 0) then None
                else CreateMapNode (NonSpecific.NonSpecificTagQT) (List.rev acc) |> this.ResolveTag  ps |> Some
            match HasMatches(ps.InputString, RGS((this.``s-indent(n)`` ps))) with
            |   (true, mt, frs) -> 
                let prs = ps.SetRestString frs
                match (this.``ns-l-block-map-entry`` prs) with
                |   Some(ck, cv, prs2) -> ``ns-l-compact-mapping`` prs2 ((ck, cv) :: acc)
                |   _ -> contentOrNone
            |   (false, _, _) -> contentOrNone
        match ps with
        |   Parse(this.``ns-l-block-map-entry``) (ck, cv, prs) -> ``ns-l-compact-mapping`` prs [(ck,cv)]
        |   _ ->    None
        |> ParseState.AddSuccessSR  "ns-l-compact-mapping" ps

    //  [196]   http://www.yaml.org/spec/1.2/spec.html#s-l+block-node(n,c)
    member this.``s-l+block-node`` (ps:ParseState) : ParseFuncSingleResult =
        logger "s-l+block-node" ps
        (ps |> ParseState.OneOf) {
            either (this.``s-l+block-in-block``)
            either (this.``s-l+flow-in-block``)
            ifneither (None)
        }
        |> ParseState.AddSuccessSR  "s-l+block-node" ps

    //  [197]   http://www.yaml.org/spec/1.2/spec.html#s-l+flow-in-block(n)
    member this.``s-l+flow-in-block`` (ps:ParseState) : ParseFuncSingleResult =
        logger "s-l+flow-in-block" ps
        let prs = ps.SetIndent (ps.n + 1)
        let prs = prs.SetStyleContext ``Flow-out``
        match (HasMatches(prs.InputString, RGS(this.``s-separate`` prs))) with
        |   (true, mt, frs) -> 
            let prs = prs.SetRestString frs
            match (prs) with
            |   Parse(this.``ns-flow-node``) (c, prs2) -> 
                match (HasMatches(prs2.InputString, RGS(this.``s-l-comments``))) with
                |   (true, mt, frs2) -> Some(c, prs2.SetRestString frs2)
                |   (false, _, _) -> Some(c, prs2) 
            |   _ -> None
        |   (false, _, _)   -> None
        |> ParseState.ResetEnvSR ps
        |> ParseState.AddSuccessSR "s-l+flow-in-block" ps

    //  [198]   http://www.yaml.org/spec/1.2/spec.html#s-l+block-in-block(n,c)
    member this.``s-l+block-in-block`` (ps:ParseState) : ParseFuncSingleResult =
        logger "s-l+block-in-block" ps
        ps.OneOf {
            either (this.``s-l+block-scalar``)
            either (this.``s-l+block-collection``)
            ifneither (None)
        }
        |> ParseState.AddSuccessSR "s-l+block-in-block" ps

    //  [199]   http://www.yaml.org/spec/1.2/spec.html#s-l+block-scalar(n,c)
    member this.``s-l+block-scalar`` (ps:ParseState) : ParseFuncSingleResult =
        logger "s-l+block-scalar" ps
        let psp1 = ps.SetIndent (ps.n + 1)
        let ``literal or folded`` (ps:ParseState) =
            let ps = ps.SetIndent (ps.n-1)
            let mapScalar (s, prs) = CreateScalarNode (NonSpecific.NonSpecificTagQT)(s) |> this.ResolveTag prs
            ps.OneOf {
                either   (this.``c-l+literal`` >> Option.map mapScalar)
                either   (this.``c-l+folded``  >> Option.map mapScalar)
                ifneither None
            }

        match HasMatches(psp1.InputString, RGS(this.``s-separate`` psp1)) with
        |   (true, mt, frs) -> 
            let prs = psp1.SetRestString frs
            match prs with
            |   Parse(this.``content with optional properties`` ``literal or folded``) value -> Some(value)
            |   _ -> None
        |   (false, _, _) -> None
        |> ParseState.ResetEnvSR ps
        |> ParseState.AddSuccessSR "s-l+block-scalar" ps

    //  [200]   http://www.yaml.org/spec/1.2/spec.html#s-l+block-collection(n,c)
    member this.``s-l+block-collection`` (ps:ParseState) : ParseFuncSingleResult =
        logger "s-l+block-collection" ps
        let omit f d1 d2 = f d1
        let psp1 = ps.SetIndent (ps.n + 1)
        let ``seq or map`` (pls:ParseState) =
            let pls = pls.SkipIfMatch(this.``s-l-comments``) 
            let pls = pls.SetIndent (ps.n)
            match pls with
            |   Parse(omit this.``l+block-sequence`` (this.``seq-spaces`` pls)) value -> Some(value)
            |   Parse(this.``l+block-mapping``)                                value -> Some(value)
            |   _ -> None
            
        let ``optional spaced content with properties`` ps =
            match HasMatches(ps.InputString, RGS(this.``s-separate`` ps)) with
            |   (true, mt, frs) -> 
                let prs = ps.SetRestString frs
                match prs with
                |   Parse(this.``content with properties`` ``seq or map``) value -> Some(value)
                |   _ -> None
            |   (false, _, _) -> None

        psp1.OneOf {
            either (``optional spaced content with properties``)
            either (``seq or map``)
            ifneither None
        }
        |> ParseState.AddSuccessSR "s-l+block-collection" ps

    //  [201]   http://www.yaml.org/spec/1.2/spec.html#seq-spaces(n,c)
    member this.``seq-spaces`` ps = 
        match ps.c with
        |   ``Block-out``   ->  ps.SetIndent (ps.n-1)
        |   ``Block-in``    ->  ps
        |   _ ->    raise (ParseException "Case is not supported")

    //  [202]   http://www.yaml.org/spec/1.2/spec.html#l-document-prefix
    member this.``l-document-prefix`` = OPT(this.``c-byte-order-mark``) + ZOM(this.``l-comment``)

    //  [203]   http://www.yaml.org/spec/1.2/spec.html#c-directives-end
    member this.``c-directives-end`` = RGP "---"

    //  [204]   http://www.yaml.org/spec/1.2/spec.html#c-document-end
    member this.``c-document-end`` = RGP "\.\.\."

    //  [205]   http://www.yaml.org/spec/1.2/spec.html#l-document-suffix
    member this.``l-document-suffix`` = this.``c-document-end`` + this.``s-l-comments``

    //  [206]   http://www.yaml.org/spec/1.2/spec.html#c-forbidden
    member this.``c-forbidden`` =
        ``start-of-line`` + ( this.``c-directives-end`` ||| this.``c-document-end``) +
        (this.``b-char`` ||| this.``s-white`` ||| ``end-of-file``)

    //  [207]   http://www.yaml.org/spec/1.2/spec.html#l-bare-document
    member this.``l-bare-document`` (ps:ParseState) : ParseFuncSingleResult = 
        logger "l-bare-document" ps
        ps 
        |> ParseState.SetIndent -1
        |> ParseState.SetStyleContext ``Block-in``
        |> this.``s-l+block-node`` (* Excluding c-forbidden content *)
        |> ParseState.AddSuccessSR "l-bare-document" ps

    //  [208]   http://www.yaml.org/spec/1.2/spec.html#l-explicit-document
    member this.``l-explicit-document`` (ps:ParseState) : ParseFuncSingleResult =
        logger "l-explicit-document" ps
        ps 
        |> ParseState.``Match and Advance`` (this.``c-directives-end``) (fun prs ->
            prs.OneOf {
                either(this.``l-bare-document``)
                ifneither (
                    let prs2 = prs.SkipIfMatch (this.``e-node`` + this.``s-l-comments``)
                    Some(this.ResolveTag prs2 PlainEmptyNode))
            }
        )
        |> ParseState.AddSuccessSR "l-explicit-document" ps

    //  [209]   http://www.yaml.org/spec/1.2/spec.html#l-directive-document
    member this.``l-directive-document`` (ps:ParseState) : ParseFuncSingleResult = 
        logger "l-directive-document" ps
        let rec readDirectives ps =
            match ( this.``l-directive`` ps) with
            |   Some(d, psr) -> readDirectives (psr |> ParseState.AddDirective d)
            |   None         -> ps
        let psr = readDirectives ps
        this.``l-explicit-document`` psr
        |> ParseState.AddSuccessSR "l-directive-document" ps

    //  [210]   http://www.yaml.org/spec/1.2/spec.html#l-any-document
    member this.``l-any-document`` (ps:ParseState) : ParseFuncSingleResult =
        logger "l-any-document" ps
        ps.OneOf {
            either(this.``l-directive-document``)
            either(this.``l-explicit-document``)
            either(this.``l-bare-document``)
            ifneither None
        }
        |> ParseState.AddSuccessSR "l-any-document" ps

    //  [211]   http://www.yaml.org/spec/1.2/spec.html#l-yaml-stream
    member this.``l-yaml-stream`` (globalTagScema:GlobalTagSchema) (input:string) = 
        let ps = ParseState.Create input globalTagScema
        logger "l-yaml-stream" ps
        ps |> 
        ParseState.``Match and Advance`` (ZOM(this.``l-document-prefix``)) (fun psr ->
            let toEmptyOrSingletonList ps = 
                function
                |   None -> (ps, [])
                |   Some (n, ps2) -> (ps2, [n])

            let rec successorDoc (ps:ParseState, nodes) =
                if ps.InputString.Length > 0 then
                    (ps |> ParseState.OneOf) {
                        either (ParseState.``Match and Advance`` (OOM(this.``l-document-suffix``) + ZOM(this.``l-document-prefix``)) (this.``l-any-document``))
                        either (ParseState.``Match and Advance`` (ZOM(this.``l-document-prefix``)) (this.``l-explicit-document``))
                        ifneither None
                    }
                    |>  function
                        |   Some (n, ps2) -> successorDoc (ps2, (n :: nodes))
                        |   None          -> (ps |> ParseState.AddMessage (Error "Cannot parse document."), nodes)
                else
                    (ps, nodes)

            psr 
            |>  this.``l-any-document`` 
            |>  toEmptyOrSingletonList psr
            |>  successorDoc
            |>  Some
        )
        |> Option.map (fun (ps, nodes) -> (nodes |> List.rev, ps))



