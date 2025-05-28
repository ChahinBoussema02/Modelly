using System;
namespace MyModelly.Models
{
    public class Table
    {
        public string Name { get; set; }
        public string Namespace { get; set; }
        public string EntityClass { get; set; }
        public List<ColumnModel> Columns { get; set; }
    }
}

