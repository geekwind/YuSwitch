namespace YuSwitch.Models;

public class EmbeddingRequest
{
    public string Model { get; set; } = "";
    public List<string> Input { get; set; } = new();
    public string? User { get; set; }
}

public class EmbeddingResponse
{
    public string Object { get; set; } = "list";
    public List<EmbeddingData> Data { get; set; } = new();
    public string Model { get; set; } = "";
    public Usage? Usage { get; set; }
}

public class EmbeddingData
{
    public string Object { get; set; } = "embedding";
    public int Index { get; set; }
    public float[] Embedding { get; set; } = Array.Empty<float>();
}
