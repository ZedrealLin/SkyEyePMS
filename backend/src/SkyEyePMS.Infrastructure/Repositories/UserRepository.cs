using Dapper;
using SkyEyePMS.Core.Models;
using SkyEyePMS.Core.Repositories;
using SkyEyePMS.Infrastructure.Database;

namespace SkyEyePMS.Infrastructure.Repositories;

public class UserRepository : IUserRepository
{
    private readonly IDbConnectionFactory _dbf;
    private readonly DbDialect _dialect;

    public UserRepository(IDbConnectionFactory dbf)
    {
        _dbf = dbf;
        _dialect = DbDialect.For(dbf.Provider);
    }

    public async Task<User?> GetByUsernameAsync(string username)
    {
        await using var conn = await _dbf.OpenAsync();
        return await conn.QuerySingleOrDefaultAsync<User>(
            "SELECT * FROM users WHERE username = @username", new { username });
    }

    public async Task<User?> GetByIdAsync(int id)
    {
        await using var conn = await _dbf.OpenAsync();
        return await conn.QuerySingleOrDefaultAsync<User>(
            "SELECT * FROM users WHERE id = @id", new { id });
    }

    public async Task<IReadOnlyList<User>> GetAllAsync()
    {
        await using var conn = await _dbf.OpenAsync();
        return (await conn.QueryAsync<User>("SELECT * FROM users ORDER BY id")).ToList();
    }

    public async Task<int> CreateAsync(User user)
    {
        await using var conn = await _dbf.OpenAsync();
        await conn.ExecuteAsync("""
            INSERT INTO users (username, password_hash, role, created_at)
            VALUES (@Username, @PasswordHash, @Role, @CreatedAt)
            """, user);
        return await conn.ExecuteScalarAsync<int>(_dialect.LastInsertIdSql);
    }

    public async Task UpdatePasswordAsync(int id, string passwordHash)
    {
        await using var conn = await _dbf.OpenAsync();
        await conn.ExecuteAsync("UPDATE users SET password_hash = @passwordHash WHERE id = @id",
            new { id, passwordHash });
    }

    public async Task UpdateLastLoginAsync(int id, DateTime at)
    {
        await using var conn = await _dbf.OpenAsync();
        await conn.ExecuteAsync("UPDATE users SET last_login_at = @at WHERE id = @id", new { id, at });
    }

    public async Task UpdateTotpAsync(int id, string? secretCipher, bool enabled)
    {
        await using var conn = await _dbf.OpenAsync();
        await conn.ExecuteAsync("UPDATE users SET totp_secret = @secret, totp_enabled = @enabled WHERE id = @id",
            new { id, secret = secretCipher, enabled });
    }

    public async Task DeleteAsync(int id)
    {
        await using var conn = await _dbf.OpenAsync();
        await conn.ExecuteAsync("DELETE FROM users WHERE id = @id", new { id });
    }

    public async Task<int> CountAsync()
    {
        await using var conn = await _dbf.OpenAsync();
        return await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM users");
    }
}
