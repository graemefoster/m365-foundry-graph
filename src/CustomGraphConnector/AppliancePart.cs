using System.Globalization;
using CsvHelper;
using CsvHelper.Configuration;
using CsvHelper.TypeConversion;

namespace CustomGraphConnector;

public sealed class AppliancePart
{
    public long PartNumber { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public double Price { get; set; }
    public long Inventory { get; set; }
    public List<string> Appliances { get; set; } = [];

    public string Id => PartNumber.ToString(CultureInfo.InvariantCulture);

    public string GetUrl(string sourceUrl) => sourceUrl.Contains("{id}", StringComparison.Ordinal)
        ? sourceUrl.Replace("{id}", Id, StringComparison.Ordinal)
        : $"{sourceUrl}#part-{Id}";

    public object ToExternalItem(string sourceUrl) => new
    {
        id = Id,
        acl = new[] { new { type = "everyone", value = "everyone", accessType = "grant" } },
        properties = new Dictionary<string, object>
        {
            ["partNumber"] = PartNumber,
            ["name"] = Name,
            ["description"] = Description,
            ["price"] = Price,
            ["inventory"] = Inventory,
            ["appliances@odata.type"] = "Collection(String)",
            ["appliances"] = Appliances,
            ["url"] = GetUrl(sourceUrl),
            ["iconUrl"] = "https://github.githubassets.com/favicons/favicon.png"
        },
        content = new
        {
            type = "text",
            value = FormattableString.Invariant(
                $"{Name}\nPart number: {PartNumber}\n{Description}\nPrice: {Price}\nInventory: {Inventory}\nAppliances: {string.Join(", ", Appliances)}")
        }
    };

    public static List<AppliancePart> Load(string path)
    {
        using var reader = new StreamReader(path);
        using var csv = new CsvReader(reader, CultureInfo.InvariantCulture);
        csv.Context.RegisterClassMap<AppliancePartMap>();
        var records = csv.GetRecords<AppliancePart>().ToList();

        if (records.Count == 0)
            throw new InvalidDataException("The CSV must contain at least one appliance part.");

        var ids = new HashSet<long>();
        foreach (var part in records)
        {
            if (part.PartNumber <= 0 || !ids.Add(part.PartNumber))
                throw new InvalidDataException($"Part number must be positive and unique: {part.PartNumber}.");
            if (string.IsNullOrWhiteSpace(part.Name) || string.IsNullOrWhiteSpace(part.Description))
                throw new InvalidDataException($"Part {part.Id} requires a name and description.");
            if (!double.IsFinite(part.Price) || part.Price < 0 || part.Inventory < 0)
                throw new InvalidDataException($"Part {part.Id} has an invalid price or inventory.");
            if (part.Appliances.Count == 0)
                throw new InvalidDataException($"Part {part.Id} requires at least one appliance.");
        }

        return records;
    }
}

public sealed class AppliancePartMap : ClassMap<AppliancePart>
{
    public AppliancePartMap()
    {
        Map(part => part.PartNumber);
        Map(part => part.Name);
        Map(part => part.Description);
        Map(part => part.Price);
        Map(part => part.Inventory);
        Map(part => part.Appliances).TypeConverter<ApplianceListConverter>();
    }
}

public sealed class ApplianceListConverter : DefaultTypeConverter
{
    public override object ConvertFromString(string? text, IReaderRow row, MemberMapData memberMapData)
        => (text ?? "").Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
}
