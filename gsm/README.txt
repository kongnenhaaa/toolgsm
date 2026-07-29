========================================================================
                      GSM PRO - PHẦN MỀM QUẢN LÝ BOX SIM & OTP
========================================================================

1. MÔ TẢ TOOL
-------------
GSM Pro là phần mềm chuyên nghiệp dùng để kết nối, điều khiển và quản lý tập trung các thiết bị Box SIM GSM (GSM Modems). Phần mềm được xây dựng trên kiến trúc đa luồng mạnh mẽ, đáp ứng nhu cầu xử lý hàng loạt thẻ SIM dùng để nhận mã OTP, nhắn tin, chạy USSD và đồng bộ với giao diện Web (Firebase).

2. TÍNH NĂNG NỔI BẬT (FEATURES)
-------------------------------
[Kết Nối & Phần Cứng]
- Quản lý đồng thời hàng chục/hàng trăm cổng COM cùng lúc không giới hạn.
- Tự động nhận diện thiết bị cắm/rút USB theo thời gian thực (Auto Port Watcher).
- Đọc và phân tích thông tin cấu hình SIM: Nhà mạng, Cường độ sóng, Số dư (TKC), Hạn sử dụng.
- Tự động Ping mạng định kỳ (Keep-alive) giúp thẻ SIM luôn giữ sóng, tránh bị nhà mạng ngắt kết nối khi cắm 24/7.

[Đọc SMS & Nhận Mã OTP]
- Tự động bắt tín hiệu SMS đến (+CMTI) và đọc tin nhắn lập tức (Race-condition free).
- Bộ lọc OTP thông minh: Tự động trích xuất mã OTP từ nhiều dịch vụ (Zalo, Telegram, Facebook, Google, Apple, TikTok, Tinder...).
- Lọc chuyên sâu Zalo OTP: Tự động chặn tin nhắn không phải Zalo, chỉ nhận mã từ tổng đài Zalo (8500, +7539). Bắt và báo lỗi chính xác nếu SĐT "Không yêu cầu mã" hoặc Firebase "Sai cú pháp".
- Whitelist / Blacklist: Cho phép chặn hoặc chỉ nhận tin nhắn từ các số điện thoại/từ khóa cụ thể (Lọc tự động tin rác ezCom, tin nhắn khuyến mãi nhà mạng).

[Tự Động Hóa (Automation)]
- USSD Auto-Detection: Tự động gọi lệnh lấy số điện thoại (*0#, *110#, *123#) tùy theo mạng Vina, Mobi, Viettel, Wintel, v.v.
- USSD Tuỳ Chỉnh Hàng Loạt: Chạy bất kỳ cú pháp USSD nào cho tất cả các cổng (có độ trễ chống treo mạng).
- Tự động cập nhật số dư (TKC) thông minh khi phát hiện tin nhắn nạp tiền.
- Chuyển hướng cuộc gọi đồng loạt (Call Forwarding) tới một danh sách số đích định sẵn.
- Nhận biết sự kiện cuộc gọi đến (Incoming Call) và kết thúc cuộc gọi (Call Ended).

[Đồng Bộ Web & Firebase]
- Đồng bộ trạng thái thiết bị thời gian thực lên Web thông qua Firebase Realtime Database.
- Tiếp nhận và xử lý mệnh lệnh gửi SMS/USSD từ giao diện Web.
- Cập nhật kết quả gửi SMS và lỗi gửi (Sai đầu số, hết tiền, lỗi thiết bị) trực tiếp về Web (Tự động xóa lỗi cũ khi kết nối cổng).

[Giao Diện & Tiện Ích]
- UI hiện đại bằng WPF Material Design, hỗ trợ Dark Mode.
- Tính năng "Click-to-copy": Bấm vào bất kỳ ô Số điện thoại hay Mã OTP nào để copy nhanh vào Clipboard.
- Lịch sử OTP (OTP History) lưu cục bộ trên file CSV, tự động dọn dẹp dữ liệu cũ (sau 10 ngày).
- Đẩy thông báo OTP trực tiếp qua Bot Telegram.
- Xem Log toàn hệ thống thời gian thực với tính năng Copy/Làm mới nhanh chóng.

3. YÊU CẦU HỆ THỐNG
-------------------
- Hệ điều hành: Windows 10 / Windows 11.
- Nền tảng: .NET 10.0 SDK.
- Phần cứng: Hub USB cắm các thiết bị GSM Modem (Quectel EC20, SIM800, v.v...).

4. CÁCH CÀI ĐẶT & KHỞI CHẠY
---------------------------
- Mở Terminal (PowerShell hoặc CMD) tại thư mục mã nguồn (chứa gsm.csproj).
- Chạy lệnh: `dotnet run`
- Nếu gặp lỗi đang chạy ngầm, hãy gõ `dotnet clean` rồi chạy lại.
- Các cài đặt nâng cao như Cấu hình Firebase, Telegram Bot, Blacklist/Whitelist có thể tùy chỉnh ngay trên giao diện Cài đặt (Settings) của tool.

5. CẤU TRÚC LƯU TRỮ DỮ LIỆU
---------------------------
- otp_history.csv: File lưu trữ lịch sử nhận mã OTP.
- system_log.txt: Nhật ký hoạt động của tool.
- settings.json: Cấu hình cá nhân hóa (Settings).
- imei_backup.xlsx: Chỉ lưu ánh xạ CCID/IMEI và PortName/IMEI; không lưu SĐT, số dư hoặc metadata thuê bao.

========================================================================
