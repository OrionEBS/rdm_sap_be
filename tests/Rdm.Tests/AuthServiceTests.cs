using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging.Abstractions;
using Rdm.Application.Auth;

namespace Rdm.Tests;

public class AuthServiceTests
{
    private readonly FakeUsers _users = new();
    private AuthService Service => new(_users, new PasswordHasher<ApiUser>(), TimeProvider.System, NullLogger<AuthService>.Instance);

    [Fact]
    public async Task Bootstrap_creates_admin_only_when_no_user_exists()
    {
        await Service.EnsureBootstrapAdminAsync("admin", "Secret@123", default);
        await Service.EnsureBootstrapAdminAsync("other", "Secret@123", default);

        _users.Items.Should().ContainSingle(u => u.Username == "admin" && u.Role == ApiRoles.Admin && u.IsActive);
        _users.Items.Single().PasswordHash.Should().NotContain("Secret@123");
    }

    [Fact]
    public async Task Valid_password_logs_in_and_records_last_login()
    {
        await Service.SaveUserAsync("sales01", "Passw0rd!", "user", true, default);

        var user = await Service.ValidateAsync("sales01", "Passw0rd!", default);

        user.Should().NotBeNull();
        user!.Role.Should().Be(ApiRoles.User);
        _users.Items.Single().LastLoginAtUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task Wrong_password_unknown_user_and_disabled_user_are_rejected()
    {
        await Service.SaveUserAsync("sales01", "Passw0rd!", ApiRoles.User, true, default);
        await Service.SaveUserAsync("locked", "Passw0rd!", ApiRoles.User, false, default);

        (await Service.ValidateAsync("sales01", "wrong-pass", default)).Should().BeNull();
        (await Service.ValidateAsync("nobody", "Passw0rd!", default)).Should().BeNull();
        (await Service.ValidateAsync("locked", "Passw0rd!", default)).Should().BeNull();
    }

    [Fact]
    public async Task Update_without_password_keeps_old_password()
    {
        await Service.SaveUserAsync("sales01", "Passw0rd!", ApiRoles.User, true, default);
        await Service.SaveUserAsync("sales01", null, ApiRoles.Admin, true, default);

        (await Service.ValidateAsync("sales01", "Passw0rd!", default))!.Role.Should().Be(ApiRoles.Admin);
    }

    [Theory]
    [InlineData("new", null, "User", "phải có mật khẩu")]
    [InlineData("new", "short", "User", "ít nhất 8")]
    [InlineData("new", "Passw0rd!", "Guest", "Role")]
    public async Task Invalid_user_data_is_rejected(string username, string? password, string role, string message)
    {
        var act = () => Service.SaveUserAsync(username, password, role, true, default);
        (await act.Should().ThrowAsync<ArgumentException>()).WithMessage($"*{message}*");
    }

    private sealed class FakeUsers : IApiUserRepository
    {
        public List<ApiUser> Items { get; } = new();

        public Task<ApiUser?> FindAsync(string username, CancellationToken ct) =>
            Task.FromResult(Items.FirstOrDefault(u => u.Username.Equals(username, StringComparison.OrdinalIgnoreCase)));

        public Task<List<ApiUser>> ListAsync(CancellationToken ct) => Task.FromResult(Items.ToList());

        public Task<int> CountAsync(CancellationToken ct) => Task.FromResult(Items.Count);

        public Task UpsertAsync(string username, string? passwordHash, string role, bool isActive, DateTime nowUtc, CancellationToken ct)
        {
            var u = Items.FirstOrDefault(x => x.Username.Equals(username, StringComparison.OrdinalIgnoreCase));
            if (u is null) Items.Add(u = new ApiUser { Username = username, CreatedAtUtc = nowUtc });
            u.PasswordHash = passwordHash ?? u.PasswordHash;
            u.Role = role;
            u.IsActive = isActive;
            u.UpdatedAtUtc = nowUtc;
            return Task.CompletedTask;
        }

        public Task SetLastLoginAsync(string username, DateTime nowUtc, CancellationToken ct)
        {
            Items.Single(x => x.Username == username).LastLoginAtUtc = nowUtc;
            return Task.CompletedTask;
        }
    }
}
