namespace OsuPlayerExporter;

public class AudioItem
{
    public int id;
    public string title;
    public string[] performers;
    public string description;
    public string audio_path;
    public string cover_path;
    public string hash;

    public AudioItem() {
        id = 0;
        title = "";
        performers = [];
        description = "";
        audio_path = "";
        cover_path = "";
        hash = "";
    }

}
