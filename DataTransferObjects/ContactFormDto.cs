using System;
using System.ComponentModel.DataAnnotations;

namespace MyModelly.DataTransferObjects
{
    public class ContactFormDto
    {
        [Required]
        public string Name { get; set; }
        [EmailAddress]
        public string? Email { get; set; }
        [Required]
        public string Message { get; set; }
    }
}

