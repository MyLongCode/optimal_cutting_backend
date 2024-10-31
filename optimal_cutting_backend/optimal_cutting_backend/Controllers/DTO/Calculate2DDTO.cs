using vega.Migrations.DAL;
using vega.Models;

namespace vega.Controllers.DTO
{
    public class Calculate2DDTO
    {
        public List<Detail2DDTO> Details { get; set; }
        public Workpiece Workpiece { get; set; }
    }
}
