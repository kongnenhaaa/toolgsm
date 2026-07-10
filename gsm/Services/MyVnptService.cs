using System;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using gsm.Models;

namespace gsm.Services;

public static class MyVnptService
{
    private static readonly HttpClient _client = new HttpClient();
    private static readonly Random _random = new Random();

    private static string GetRandomDeviceModel()
    {
        int type = _random.Next(5);
        switch (type)
        {
            case 0: return $"SM-G{_random.Next(900, 999)}F";
            case 1: return $"SM-A{_random.Next(10, 99)}5F";
            case 2: return $"Pixel {_random.Next(4, 8)}";
            case 3: return $"CPH{_random.Next(2000, 2500)}";
            case 4: return $"Redmi Note {_random.Next(7, 12)}";
            default: return "motog(7)";
        }
    }

    private static string GetRandomDeviceInfo()
    {
        string deviceId = Guid.NewGuid().ToString();
        string model = GetRandomDeviceModel();
        int osVersion = _random.Next(9, 14);
        return $"{deviceId}|{deviceId}|unknown|Android||3.3.97.Prd|{model}|{osVersion}|";
    }

    private static string GetRandomUserAgent()
    {
        return $"okhttp/4.{_random.Next(7, 12)}.{_random.Next(0, 5)}";
    }

    public static async Task SetPasswordAsync(string portName, string phone, string otp, Action<string, string> addLogCallback, Action<bool, string>? onComplete = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(phone) || phone == "Chưa lấy được số")
            {
                addLogCallback?.Invoke($"[{portName}] Không có SĐT hợp lệ để đổi MK MyVNPT", "ERROR");
                return;
            }

            if (phone.StartsWith("0"))
            {
                phone = "84" + phone.Substring(1);
            }

            string pwd = "123456a@A";
            try
            {
                string passPath = AppPaths.ForRuntimeFile("input_kiemtra.txt");
                if (System.IO.File.Exists(passPath))
                {
                    string filePass = System.IO.File.ReadAllText(passPath).Trim();
                    if (!string.IsNullOrEmpty(filePass))
                    {
                        pwd = filePass;
                    }
                }
                else
                {
                    System.IO.File.WriteAllText(passPath, pwd);
                }
            }
            catch { }
            string hashedPwd = CreateMD5(pwd).ToUpper();
            string deviceInfo = GetRandomDeviceInfo();
            string userAgent = GetRandomUserAgent();

            // 1. Kiểm tra tài khoản để biết gọi api nào
            var checkPayload = new { msisdn = phone };
            string checkJson = JsonSerializer.Serialize(checkPayload);
            using var checkRequest = new HttpRequestMessage(HttpMethod.Post, "https://api-myvnpt.vnpt.vn/mapi_v2/services/authen_check_account");
            checkRequest.Content = new StringContent(checkJson, Encoding.UTF8, "application/json");
            checkRequest.Headers.TryAddWithoutValidation("Authorization", "Bearer a60bd62fed0cf1076e93af76114f196bd9c5a48155b2bac88afe15c49595414b");
            checkRequest.Headers.TryAddWithoutValidation("Device-Info", deviceInfo);
            checkRequest.Headers.TryAddWithoutValidation("Language", "vi_VN");
            checkRequest.Headers.TryAddWithoutValidation("User-Agent", userAgent);

            var checkResponse = await _client.SendAsync(checkRequest);
            string checkResponseContent = await checkResponse.Content.ReadAsStringAsync();
            bool accountExists = checkResponseContent.Contains("\"error_code\":\"3\"") || checkResponseContent.Contains("\"error_code\": \"3\"");

            // 2. Gọi api set pass tương ứng
            string targetUrl = accountExists 
                ? "https://api-myvnpt.vnpt.vn/mapi_v2/services/authen_miss_password" 
                : "https://api-myvnpt.vnpt.vn/mapi_v2/services/authen_register";

            object payload;
            if (accountExists)
            {
                payload = new
                {
                    msisdn = phone,
                    otp = otp,
                    password = hashedPwd
                };
            }
            else
            {
                payload = new
                {
                    msisdn = phone,
                    password = hashedPwd,
                    pin = otp
                };
            }

            string json = JsonSerializer.Serialize(payload);

            using var request = new HttpRequestMessage(HttpMethod.Post, targetUrl);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");

            request.Headers.TryAddWithoutValidation("Authorization", "Bearer a60bd62fed0cf1076e93af76114f196bd9c5a48155b2bac88afe15c49595414b");
            request.Headers.TryAddWithoutValidation("Cache-Control", "no-cache");
            request.Headers.TryAddWithoutValidation("Device-Info", deviceInfo);
            request.Headers.TryAddWithoutValidation("Language", "vi_VN");
            request.Headers.TryAddWithoutValidation("User-Agent", userAgent);

            var response = await _client.SendAsync(request);
            var responseContent = await response.Content.ReadAsStringAsync();

            string modeStr = accountExists ? "Quên mật khẩu" : "Tạo mới tài khoản";
            if (responseContent.Contains("\"error_code\":\"0\"") || responseContent.Contains("\"errorCode\":\"0\"") || responseContent.Contains("\"error_code\": \"0\""))
            {
                addLogCallback?.Invoke($"[{portName}] Đặt mật khẩu MyVNPT {phone} thành công ({modeStr})! Pass: {pwd}", "SUCCESS");
                string logPath = AppPaths.ForRuntimeFile("kiemtra.txt");
                System.IO.File.AppendAllText(logPath, $"{phone}|{pwd}\n");
                onComplete?.Invoke(true, "Kiểm tra thành công");
            }
            else
            {
                addLogCallback?.Invoke($"[{portName}] Đặt mật khẩu MyVNPT {phone} thất bại ({modeStr}): {responseContent}", "ERROR");
                onComplete?.Invoke(false, $"Kiểm tra thất bại");
            }
        }
        catch (Exception ex)
        {
            addLogCallback?.Invoke($"[{portName}] Lỗi đặt mật khẩu MyVNPT: {ex.Message}", "ERROR");
            onComplete?.Invoke(false, $"Lỗi kiểm tra");
        }
    }

    private static string CreateMD5(string input)
    {
        using (MD5 md5 = MD5.Create())
        {
            byte[] inputBytes = Encoding.UTF8.GetBytes(input);
            byte[] hashBytes = md5.ComputeHash(inputBytes);

            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < hashBytes.Length; i++)
            {
                sb.Append(hashBytes[i].ToString("x2"));
            }
            return sb.ToString();
        }
    }
}
