========================================================================
                      GSM PRO - PHẦN MỀM QUẢN LÝ BOX SIM & OTP
========================================================================

1. MÔ TẢ TOOL
-------------
GSM Pro là phần mềm chuyên nghiệp dùng để kết nối và quản lý các thiết bị 
Box SIM GSM (cắm qua cổng USB/COM). Các tính năng chính bao gồm:
- Đọc, quản lý nhiều cổng COM cùng lúc.
- Tự động bắt tín hiệu +CMTI để xử lý tin nhắn xen ngang (Race condition free).
- Trích xuất mã OTP từ tin nhắn tự động.
- Gửi thông báo OTP ngay lập tức qua Telegram.
- Xem tình trạng SIM (Sóng, Nhà mạng, IMEI, Serial, Số dư, Hạn sử dụng).
- Hỗ trợ đa luồng (Multi-threading) bằng SemaphoreSlim siêu mượt.
- Giao diện Material Design hiện đại bằng WPF, chạy mượt mà.

2. YÊU CẦU HỆ THỐNG
-------------------
- Hệ điều hành: Windows 10 / Windows 11.
- Nền tảng: .NET SDK 8.0 / 10.0.
- Phần cứng: Hub USB cắm các thiết bị GSM Modem (như Quectel, SIM800, v.v...).

3. CÁCH CÀI ĐẶT
---------------
Phần mềm này là dạng Portable (Chạy trực tiếp từ mã nguồn) nên không cần 
cài đặt phức tạp. Bạn chỉ cần:
- Tải toàn bộ mã nguồn về thư mục máy tính.
- Cài đặt .NET SDK từ trang chủ Microsoft (nếu máy chưa có).
- Cắm thiết bị Box SIM vào máy tính, chờ Windows nhận diện Cổng COM.

4. CÁCH CHẠY
------------
- Mở Terminal (PowerShell hoặc CMD) tại thư mục chứa mã nguồn (thư mục có file gsm.csproj).
- Gõ lệnh:
    dotnet run

  (Lưu ý: Nếu bạn vừa ngắt ứng dụng bằng Ctrl+C và gặp lỗi "more than one project file", 
  hãy gõ lệnh: dotnet clean, sau đó chạy lại lệnh dotnet run).

5. CẤU HÌNH TELEGRAM (Dành cho Developer)
-----------------------------------------
Mở file `Services/TelegramService.cs` và cập nhật thông tin Bot của bạn:
- BotToken: Token lấy từ BotFather (vd: 8926115937:AA...)
- ChatId: ID tài khoản Telegram cá nhân của bạn (vd: 7035960212)
(Bot phải được bấm START trước khi phần mềm có thể nhắn tin cho bạn).

6. CHẾ ĐỘ GIẢ LẬP (TEST)
------------------------
Trong tab GSM, bấm vào nút màu cam "Test OTP (Máy ảo)" để giả lập một 
tin nhắn SMS. Hệ thống sẽ tự động bắt OTP và đẩy thông báo qua Telegram 
mà không cần cắm Box SIM thực tế.

========================================================================
