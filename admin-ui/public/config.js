// Cấu hình lúc chạy — SỬA ĐƯỢC SAU KHI DEPLOY, không cần build lại.
// apiBaseUrl: địa chỉ RDM API. Trống = cùng origin với trang (dev: proxy Vite; hoặc FE + API sau cùng reverse proxy).
// VD: apiBaseUrl: 'https://rdm-api.tafico.local:8443'  — nhớ thêm origin của trang này vào Cors:AllowedOrigins của API.
window.RDM_CONFIG = {
  apiBaseUrl: ''
};
