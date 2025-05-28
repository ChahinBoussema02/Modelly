using System;
namespace MyModelly.Models
{
    public class ColumnModel
    {
        public string Name { get; set; }
        public string DataType { get; set; }
        public bool IsAutoIncrement { get; set; }
        public bool IsPrimaryKey { get; set; }
        
    }

}

