﻿using AutoMapper;
using OpenSmsPlatform.Model;

namespace OpenSmsPlatform.Extensions
{
    public class CustomProfile : Profile
    {
        /// <summary>
        /// 配置构造函数，用来创建关系映射
        /// </summary>
        public CustomProfile()
        {
            CreateMap<AuditSqlLog, AuditSqlLogVo>();
            CreateMap<AuditSqlLogVo, AuditSqlLog>();
        }
    }
}
