using System.Collections.Generic;
#nullable disable

namespace circuits
{
    [ProtoBuf.ProtoContract]
    public class CircuitsLinkDelta
    {
        [ProtoBuf.ProtoMember(1)] public string FromNodeIdN;
        [ProtoBuf.ProtoMember(2)] public string FromPortId;
        [ProtoBuf.ProtoMember(3)] public int FromX;
        [ProtoBuf.ProtoMember(4)] public int FromY;
        [ProtoBuf.ProtoMember(5)] public int FromZ;
        [ProtoBuf.ProtoMember(6)] public int FromDim;

        [ProtoBuf.ProtoMember(7)] public string ToNodeIdN;
        [ProtoBuf.ProtoMember(8)] public string ToPortId;
        [ProtoBuf.ProtoMember(9)] public int ToX;
        [ProtoBuf.ProtoMember(10)] public int ToY;
        [ProtoBuf.ProtoMember(11)] public int ToZ;
        [ProtoBuf.ProtoMember(12)] public int ToDim;

        [ProtoBuf.ProtoMember(13)] public bool Added;
    }

    [ProtoBuf.ProtoContract]
    public class CircuitsSnapshot
    {
        [ProtoBuf.ProtoMember(1)] public List<CircuitsLinkDelta> Links;
    }

    [ProtoBuf.ProtoContract]
    public class CircuitsRequestLink
    {
        [ProtoBuf.ProtoMember(1)] public string FromNodeIdN;
        [ProtoBuf.ProtoMember(2)] public string FromPortId;
        [ProtoBuf.ProtoMember(3)] public string ToNodeIdN;
        [ProtoBuf.ProtoMember(4)] public string ToPortId;
        [ProtoBuf.ProtoMember(5)] public int FromX;
        [ProtoBuf.ProtoMember(6)] public int FromY;
        [ProtoBuf.ProtoMember(7)] public int FromZ;
        [ProtoBuf.ProtoMember(8)] public int FromDim;
        [ProtoBuf.ProtoMember(9)] public int ToX;
        [ProtoBuf.ProtoMember(10)] public int ToY;
        [ProtoBuf.ProtoMember(11)] public int ToZ;
        [ProtoBuf.ProtoMember(12)] public int ToDim;
        [ProtoBuf.ProtoMember(13)] public bool HasPositions;
    }

    [ProtoBuf.ProtoContract]
    public class CircuitsRequestClearPort
    {
        [ProtoBuf.ProtoMember(1)] public string NodeIdN;
        [ProtoBuf.ProtoMember(2)] public string PortId;
        [ProtoBuf.ProtoMember(3)] public int Mode;
    }

    [ProtoBuf.ProtoContract]
    public class CircuitsRequestSnapshot { }

    [ProtoBuf.ProtoContract]
    public class CircuitsSaveDto
    {
        [ProtoBuf.ProtoMember(1)] public List<LinkDto> Links = new();
        [ProtoBuf.ProtoMember(2)] public List<OutputValueDto> OutputValues = new();
    }

    [ProtoBuf.ProtoContract]
    public class LinkDto
    {
        [ProtoBuf.ProtoMember(1)] public string FromNodeIdN;
        [ProtoBuf.ProtoMember(2)] public string FromPortId;
        [ProtoBuf.ProtoMember(3)] public string ToNodeIdN;
        [ProtoBuf.ProtoMember(4)] public string ToPortId;
        [ProtoBuf.ProtoMember(5)] public int FromX;
        [ProtoBuf.ProtoMember(6)] public int FromY;
        [ProtoBuf.ProtoMember(7)] public int FromZ;
        [ProtoBuf.ProtoMember(8)] public int FromDim;
        [ProtoBuf.ProtoMember(9)] public int ToX;
        [ProtoBuf.ProtoMember(10)] public int ToY;
        [ProtoBuf.ProtoMember(11)] public int ToZ;
        [ProtoBuf.ProtoMember(12)] public int ToDim;
        [ProtoBuf.ProtoMember(13)] public bool HasPositions;
    }

    [ProtoBuf.ProtoContract]
    public class OutputValueDto
    {
        [ProtoBuf.ProtoMember(1)] public string NodeIdN;
        [ProtoBuf.ProtoMember(2)] public string PortId;
        [ProtoBuf.ProtoMember(3)] public int SignalType;
        [ProtoBuf.ProtoMember(4)] public bool BoolValue;
        [ProtoBuf.ProtoMember(5)] public int IntValue;
        [ProtoBuf.ProtoMember(6)] public float FloatValue;
        [ProtoBuf.ProtoMember(7)] public string StringValue;
    }
}
