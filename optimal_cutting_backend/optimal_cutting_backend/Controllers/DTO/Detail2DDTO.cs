using CsvHelper.Configuration.Attributes;

namespace vega.Controllers.DTO
{
    public class Detail2DDTO
    {
        [Name("Length")]
        [Index(0)]
        public int Length { get; set; }
        [Name("Width")]
        [Index(1)]
        public int Width{ get; set; }
        [Name("Count")]
        [Index(2)]
        public int Count { get; set; }
    }
}
