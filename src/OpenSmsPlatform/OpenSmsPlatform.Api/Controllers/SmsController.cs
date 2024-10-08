﻿using IdGen;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using OpenSmsPlatform.Common;
using OpenSmsPlatform.Common.Helper;
using OpenSmsPlatform.IService;
using OpenSmsPlatform.Model;
using OpenSmsPlatform.Repository.UnitOfWorks;
using SmsPackage.Model;
using SmsPackage.Service;

namespace OpenSmsPlatform.Api.Controllers
{
    /// <summary>
    /// 短信控制器（主接口）
    /// </summary>
    [Route("api/[controller]/[action]")]
    [ApiController]
    public class SmsController : ControllerBase
    {
        private readonly IOspAccountService _accountService;
        private readonly IOspRecordService _recordService;
        private readonly IOspLimitService _limitService;
        private readonly IUnitOfWorkManage _unitOfWorkManage;
        private readonly IHttpContextAccessor _httpContext;
        private readonly IZhutongService _zhutongService;
        private readonly ILianluService _lianluService;

        public SmsController(IOspAccountService accountService,
            IOspRecordService recordService,
            IOspLimitService limitService,
            IZhutongService zhutongService,
            ILianluService lianluService,
            IUnitOfWorkManage unitOfWorkManage,
            IHttpContextAccessor httpContext,
            IConfiguration config)
        {
            _accountService = accountService;
            _recordService = recordService;
            _limitService = limitService;
            _zhutongService = zhutongService;
            _lianluService = lianluService;
            _unitOfWorkManage = unitOfWorkManage;
            _httpContext = httpContext;
        }

        /// <summary>
        /// 发送短信
        /// </summary>
        /// <param name="request">短信请求对象</param>
        /// <returns></returns>
        [HttpPost]
        public async Task<ApiResponse> SendAsync([FromBody] SmsRequest request)
        {
            ApiResponse response = new ApiResponse();

            try
            {
                //1.检查短信参数
                request.Mobiles = request.Mobiles.Where(p => p.Length == 11).ToList();  //过滤手机号
                var turple = CheckSmsParam(request);
                if (!turple.enable)
                {
                    response.Message = turple.msg;
                    return response;
                }

                //2.验证token
                if (!await _accountService.ValidAccount(request.AccId, request.TimeStamp, request.Signature))
                {
                    response.Code = 401;
                    response.Message = "Signature Error";
                    return response;
                }

                //3.(单发时)短信限制
                if (request.Mobiles.Count == 1)
                {
                    (bool enable, string msg) limitTruple = await CheckSendLimit(request.Mobiles[0], request.Code);
                    if (!limitTruple.enable)
                    {
                        response.Message = limitTruple.msg;
                        return response;
                    }
                }

                //4.检查账号
                int UseCounts = CalcUseCounts(request.Mobiles.Count, request.Contents.Length);
                (bool enable, string msg, OspAccount account) acountTuple = await CheckAccount(request, UseCounts);
                if (!acountTuple.enable)
                {
                    response.Message = acountTuple.msg;
                    return response;
                }
                OspAccount OspAccount = acountTuple.account;

                //5.发送短信
                if (OspAccount.ApiCode == "lianlu")
                {
                    LianLuApiResponse lianLuResponse = await _lianluService.Send(request.Mobiles, request.Contents, request.SmsSuffix);
                    response.Code = lianLuResponse.Code;
                    response.Message = lianLuResponse.Message;
                }
                else if (OspAccount.ApiCode == "zhutong")
                {
                    ZhuTongApiResponse zhutongResponse = await _zhutongService.Send(request.Mobiles, request.Contents);
                    response.Code = zhutongResponse.Code;
                    response.Message = zhutongResponse.Message;
                }
                else { response.Code = 200; } //本地测试，不实际发送，只走业务


                if (response.Code == 200)
                {
                    //6. 扣费、保存记录(事务提交)
                    using var uow = _unitOfWorkManage.CreateUnitOfWork();
                    OspAccount.AccCounts = OspAccount.AccCounts - UseCounts;
                    bool flag = await _recordService.AddRecordsAndUpdateAmount(AppendList(request, OspAccount), OspAccount);

                    uow.Commit();

                    //7.最后返回
                    response.Message = response.Message ?? "发送成功";
                }
                return response;
            }
            catch (Exception ex)
            {
                response.Code = 500;
                response.Message = $"内部服务器错误: {ex.Message}";
                return response;
            }
        }

        /// <summary>
        /// 检查短信参数
        /// </summary>
        /// <param name="request"></param>
        /// <returns>元组</returns>
        private (bool enable, string msg) CheckSmsParam(SmsRequest request)
        {
            (bool enable, string msg) checkTurple = (false, string.Empty);

            if (string.IsNullOrEmpty(request.AccId.Trim())
                || string.IsNullOrEmpty(request.Contents.Trim())
                || string.IsNullOrEmpty(request.SmsSuffix.Trim())
                || string.IsNullOrEmpty(request.Signature.Trim())
                || request.Mobiles.Count == 0)
            {
                checkTurple.msg = "参数缺失";
            }
            else if (!request.Contents.Contains(request.Code) && !string.IsNullOrEmpty(request.Code))
            {
                checkTurple.msg = "内容不包含验证码";
            }
            else if (!string.IsNullOrWhiteSpace(request.Contents) && !request.Contents.Contains(request.SmsSuffix))
            {
                checkTurple.msg = "内容缺少短信后缀";
            }
            else if (request.Mobiles.Count > 1000)
            {
                checkTurple.msg = "手机号码不可超过1000个";
            }
            else if (request.Contents.Length > 1000)
            {
                checkTurple.msg = "内容不可超过1000个字符";
            }
            else { checkTurple.enable = true; }

            return checkTurple;
        }

        /// <summary>
        /// 计算使用条数
        /// </summary>
        /// <param name="mobileCounts">手机号码数目</param>
        /// <param name="contentLength">内容长度</param>
        /// <returns></returns>
        private int CalcUseCounts(int mobileCounts, int contentLength)
        {
            int needCount = mobileCounts;              //小于70个字符，直接返回手机号码数
            if (contentLength > 70)
            {
                int SingleCount = contentLength / 67;  //先取商
                if ((contentLength % 67) > 0)          //再取余，有余数需要+1
                {
                    SingleCount = SingleCount + 1;
                }
                needCount = SingleCount * mobileCounts;
            }

            return needCount;
        }

        /// <summary>
        /// 检查账号
        /// </summary>
        /// <param name="request"></param>
        /// <param name="useCounts"></param>
        /// <returns></returns>
        private async Task<(bool enable, string msg, OspAccount account)> CheckAccount(SmsRequest request, int useCounts)
        {
            (bool enable, string msg, OspAccount account) checkTurple = (false, string.Empty, null);

            OspAccount smsAccount = await _accountService.QueryOspAcount(request.AccId, request.SmsSuffix);
            if (smsAccount == null)
            {
                checkTurple.msg = "签名错误";
            }
            if (smsAccount.IsEnable == 2)
            {
                checkTurple.msg = "账号停用";
            }
            else if (smsAccount.AccCounts < useCounts)
            {
                checkTurple.msg = "余额不足";
            }
            else
            {
                checkTurple.enable = true;
                checkTurple.account = smsAccount;
            }

            return checkTurple;
        }

        /// <summary>
        /// 拼接列表
        /// </summary>
        /// <param name="request"></param>
        /// <param name="account"></param>
        /// <returns></returns>
        private List<OspRecord> AppendList(SmsRequest request, OspAccount account)
        {
            var generator = new IdGenerator(0);
            string ip = IpHelper.GetIp(_httpContext);
            List<OspRecord> list = new List<OspRecord>();
            foreach (var mobile in request.Mobiles)
            {
                OspRecord record = new OspRecord();

                record.Id = generator.CreateId();
                record.AccId = account.Id;
                record.Mobile = mobile;
                record.Content = request.Contents;
                record.Code = request.Code;
                record.IsCode = string.IsNullOrEmpty(request.Code) ? 2 : 1;
                record.IsUsed = 2;
                record.SendOn = DateTime.Now;
                record.Counts = CalcUseCounts(request.Mobiles.Count, request.Contents.Length);
                record.RequestId = request.RequestId;
                record.ApiCode = account.ApiCode;
                record.CreateOn = DateTime.Now;
                record.CreateBy = "admin";
                record.CreateUid = 0;
                record.RequestId = ip;

                list.Add(record);
            }
            return list;
        }

        /// <summary>
        /// 检查发送显示
        /// </summary>
        /// <param name="mobile">手机号码</param>
        /// <param name="code">验证码</param>
        /// <returns></returns>
        private async Task<(bool enable, string msg)> CheckSendLimit(string mobile, string code)
        {
            (bool enable, string msg) resultTurple = (true, string.Empty);
            int smsType = 1;
            if (string.IsNullOrEmpty(code))
            {
                smsType = 2;
            }

            //判断是否在限制名单中（不在名单中，走条数限制逻辑）
            OspLimit ospLimit = await _limitService.IsInLimitList(mobile);
            if (ospLimit == null)
            {
                List<SmsLimitConfig> list = AppSettings.App<SmsLimitConfig>("SmsLimit");
                SmsLimitConfig smsLimit = list.Where(x => x.SmsType == smsType).SingleOrDefault();
                if (smsLimit.Enabled)
                {
                    PageModel<OspRecord> page = await _recordService.QueryMonthlyRecords(mobile, DateTime.Now, smsLimit.MonthMaxCount, smsType);
                    var todaySendList = page.data.Where(x => x.CreateOn.Date == DateTime.Today).ToList();

                    if (page.dataCount >= smsLimit.MonthMaxCount)
                    {
                        resultTurple.enable = false;
                        resultTurple.msg = "达到当月发送最大值";
                    }
                    else if (todaySendList.Count() >= smsLimit.DayMaxCount)
                    {
                        resultTurple.enable = false;
                        resultTurple.msg = "达到当日发送最大值";
                    }
                }
            }
            else if (ospLimit.LimitType == 2) //黑名单
            {
                resultTurple.enable = false;
                resultTurple.msg = "the phone number is on the blacklist";
            }

            return resultTurple;
        }
    }
}
