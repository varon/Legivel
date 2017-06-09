﻿module RepresentationGraph

open System.Diagnostics
open YamlParser.Internals

type NodeKind = 
    | Mapping
    | Sequence
    | Scalar

[<NoEquality; NoComparison>]
[<StructuredFormatDisplay("{AsString}")>]
type GlobalTag = {
        Uri     : string
        Kind    : NodeKind
        Regex   : string
        canonFn : string -> string
    }
    with
        static member Create (uri, nk, rgx, canon) =
            { 
                Uri = uri; 
                Kind = nk;
                Regex = sprintf "\\A(%s)\\z" rgx
                canonFn = canon
            }

        static member Create (uri, nk, rgx) = GlobalTag.Create (uri, nk, rgx, fun s -> s)

        static member Create (uri, nk) = GlobalTag.Create (uri, nk, ".*", fun s -> s)

        member this.Canonical s = this.canonFn s

        override this.ToString() = sprintf "<%A::%s>" this.Kind this.Uri
        member m.AsString = m.ToString()

[<NoEquality; NoComparison>]
[<StructuredFormatDisplay("{AsString}")>]
type TagKind =
    |   Global       of GlobalTag
    |   Unrecognized of GlobalTag
    |   Local        of string
    |   NonSpecific  of string
    with
        override this.ToString() =
            match this with
            |   Global       gt -> sprintf "Global:%O" gt
            |   Unrecognized gt -> sprintf "Unrecognized:%O" gt
            |   Local        ls -> sprintf "Local:%O" ls
            |   NonSpecific  ls -> sprintf "NonSpecific:%O" ls
        member m.AsString = m.ToString()

[<NoEquality; NoComparison>]
[<StructuredFormatDisplay("{AsString}")>]
type NodeData<'T> = {
        Tag  : TagKind
        Data : 'T
        Hash : Lazy<NodeHash>
    }
    with
        static member Create t d h =
            { Tag = t; Data = d; Hash = h}

        member this.SetTag t = 
            { this with Tag = t}

        override this.ToString() = sprintf "(%A) %O %A" (this.Hash.Force()) (this.Tag) (this.Data)
        member m.AsString = m.ToString()

[<DebuggerDisplay("{this.DebuggerInfo}")>]
[<NoEquality; NoComparison>]
type Node =
    | SeqNode of NodeData<Node list>
    | MapNode of NodeData<(Node*Node) list>
    | ScalarNode of NodeData<string>
    with
        member private this.tagString t =
            match t with
            |   Global gt -> gt.Uri
            |   Local  s  -> s
            |   NonSpecific s -> s
            |   Unrecognized gt -> gt.Uri

        member this.Hash 
            with get() =
                match this with
                |   SeqNode n       -> n.Hash
                |   MapNode n       -> n.Hash
                |   ScalarNode n    -> n.Hash
        
        member this.SetTag t = 
            match this with
            |   SeqNode n       -> SeqNode(n.SetTag t)
            |   MapNode n       -> MapNode(n.SetTag t)
            |   ScalarNode n    -> ScalarNode(n.SetTag t)

        member this.NodeTag 
            with get() =
                match this with
                |   SeqNode n       -> n.Tag
                |   MapNode n       -> n.Tag
                |   ScalarNode n    -> n.Tag

        member this.Kind
            with get() =
                match this with
                |   SeqNode _       -> Sequence
                |   MapNode _       -> Mapping
                |   ScalarNode _    -> Scalar

        member this.DebuggerInfo 
            with get() =
                match this with
                |   SeqNode d       -> sprintf "<%s>[..], length=%d" (this.tagString d.Tag) d.Data.Length
                |   MapNode d       -> sprintf "<%s>{..}, length=%d" (this.tagString d.Tag) d.Data.Length
                |   ScalarNode d    -> sprintf "<%s>\"%s\"" (this.tagString d.Tag) d.Data


type Legend = {
        YamlVersion : string
    }




