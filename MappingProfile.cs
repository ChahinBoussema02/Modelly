using System;
using Microsoft.AspNetCore.Identity;
using MyModelly.DataTransferObjects;
using MyModelly.Models;
using AutoMapper;
using static Microsoft.EntityFrameworkCore.DbLoggerCategory.Database;

namespace MyModelly
{
	public class MappingProfile:Profile
	{
        public MappingProfile()
        {
            CreateMap<UserForRegistrationDto, IdentityUser>()
                .ForMember(u => u.UserName, opt => opt.MapFrom(x => x.Email));
        }
    }
}

