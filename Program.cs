using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.Tokens;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson.Serialization.Conventions;
using MongoDB.Driver;
using Google.Apis.Auth;
using Microsoft.OpenApi;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);

builder.Services.AddCors(options =>
{
    options.AddPolicy("SeenGoCorsPolicy", policy =>
    {
        policy.WithOrigins("http://localhost:4200", "https://seengo-frontweb-production.up.railway.app")
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

var conventionPack = new ConventionPack { new IgnoreIfNullConvention(true) };
ConventionRegistry.Register("SeenGoConventions", conventionPack, t => true);

var mongoSettings = builder.Configuration.GetSection("MongoDbSettings");
var mongoConnectionString = mongoSettings["ConnectionString"];
if (string.IsNullOrWhiteSpace(mongoConnectionString))
    throw new InvalidOperationException("Falta MongoDbSettings:ConnectionString. Configura la variable de entorno MongoDbSettings__ConnectionString o appsettings.Local.json.");

builder.Services.AddSingleton<IMongoClient>(sp => new MongoClient(mongoConnectionString));
builder.Services.AddScoped(sp =>
{
    var client = sp.GetRequiredService<IMongoClient>();
    return client.GetDatabase(mongoSettings["DatabaseName"]);
});

var jwtSettings = builder.Configuration.GetSection("Jwt");
var jwtSecret = jwtSettings["SecretKey"];
if (string.IsNullOrWhiteSpace(jwtSecret))
    throw new InvalidOperationException("Falta Jwt:SecretKey. Configura la variable de entorno Jwt__SecretKey o appsettings.Local.json.");

var secretKey = Encoding.UTF8.GetBytes(jwtSecret);

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtSettings["Issuer"],
            ValidAudience = jwtSettings["Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(secretKey)
        };
    });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AdminOnly", policy => policy.RequireRole("admin"));
});

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new ObjectIdJsonConverter());
});
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.AddSecurityDefinition("Bearer", new Microsoft.OpenApi.Models.OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = Microsoft.OpenApi.Models.SecuritySchemeType.ApiKey,
        Scheme = "Bearer",
        BearerFormat = "JWT",
        In = Microsoft.OpenApi.Models.ParameterLocation.Header,
        Description = "Ingresa 'Bearer' [espacio] y luego tu token.\n\nEjemplo: \"Bearer eyJhbGci...\""
    });

    c.AddSecurityRequirement(new Microsoft.OpenApi.Models.OpenApiSecurityRequirement
    {
        {
            new Microsoft.OpenApi.Models.OpenApiSecurityScheme
            {
                Reference = new Microsoft.OpenApi.Models.OpenApiReference
                {
                    Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme,
                    Id = "Bearer"
                }
            },
            Array.Empty<string>()
        }
    });
});

var app = builder.Build();

app.UseCors("SeenGoCorsPolicy");
if (!app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}
app.UseAuthentication();
app.UseAuthorization();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<IMongoDatabase>();
    var catalogCollection = db.GetCollection<GestureCatalogDocument>("gesture_catalog");

    var catalogoFijo = new[]
    {
        new GestureCatalogDocument { Name = "palma_abierta", Label = "Palma abierta" },
        new GestureCatalogDocument { Name = "puno", Label = "Puño" },
        new GestureCatalogDocument { Name = "paz", Label = "Paz (✌)" },
        new GestureCatalogDocument { Name = "like", Label = "Pulgar arriba" },
        new GestureCatalogDocument { Name = "loser", Label = "L (loser)" },
        new GestureCatalogDocument { Name = "rock", Label = "Rock 🤘" },
        new GestureCatalogDocument { Name = "dedo_anular", Label = "Dedo anular" },
    };

    foreach (var gesto in catalogoFijo)
    {
        var filtro = Builders<GestureCatalogDocument>.Filter.Eq(g => g.Name, gesto.Name);
        var update = Builders<GestureCatalogDocument>.Update.Set(g => g.Label, gesto.Label);
        await catalogCollection.UpdateOneAsync(filtro, update, new UpdateOptions { IsUpsert = true });
    }
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "Seen Go API v1");
        c.RoutePrefix = string.Empty;
    });
}

// ==========================================
// TELEMETRY ENDPOINTS
// ==========================================

app.MapPost("/api/telemetry/event", async (HttpContext httpContext, [FromBody] DeviceEventDto eventDto, IMongoDatabase db, IConfiguration config) =>
{
    if (!ServiceAuth.IsValid(httpContext, config))
        return Results.Unauthorized();

    var sessionCollection = db.GetCollection<UserSessionDocument>("user_sessions");

    var filter = Builders<UserSessionDocument>.Filter.And(
        Builders<UserSessionDocument>.Filter.Eq(s => s.UserId, eventDto.UserId),
        Builders<UserSessionDocument>.Filter.Eq(s => s.DateString, DateTime.UtcNow.ToString("yyyy-MM-dd"))
    );

    var update = Builders<UserSessionDocument>.Update.Push(s => s.DeviceHistory, new DeviceLog
    {
        DeviceId = eventDto.DeviceId,
        DeviceType = eventDto.DeviceType,
        KwhConsumed = eventDto.KwhConsumed,
        IsRedundantTurnOn = eventDto.IsRedundantTurnOn,
        Timestamp = DateTime.UtcNow
    });

    await sessionCollection.UpdateOneAsync(filter, update, new UpdateOptions { IsUpsert = true });
    return Results.Ok(new { message = "Telemetría registrada exitosamente en MongoDB." });
})
.WithName("RegisterTelemetryEvent");

app.MapGet("/api/telemetry/device/{deviceId}", async (string deviceId, [FromQuery] string userId, [FromQuery] int? limit, IMongoDatabase db) =>
{
    var sessionCollection = db.GetCollection<UserSessionDocument>("user_sessions");

    var filter = Builders<UserSessionDocument>.Filter.And(
        Builders<UserSessionDocument>.Filter.Eq(s => s.UserId, userId),
        Builders<UserSessionDocument>.Filter.Eq("DeviceHistory.DeviceId", deviceId)
    );

    var sort = Builders<UserSessionDocument>.Sort.Descending(s => s.DateString);
    var sessions = await sessionCollection.Find(filter).Sort(sort).Limit(limit ?? 30).ToListAsync();

    var logs = sessions
        .SelectMany(s => s.DeviceHistory
            .Where(d => d.DeviceId == deviceId)
            .Select(d => new
            {
                d.DeviceId,
                d.DeviceType,
                d.KwhConsumed,
                d.IsRedundantTurnOn,
                d.Timestamp,
                sessionDate = s.DateString
            }))
        .OrderByDescending(x => x.Timestamp)
        .ToList();

    return Results.Ok(logs);
})
.WithName("GetDeviceTelemetry")
.RequireAuthorization();

// ==========================================
// PREDICTIVE SUGGESTIONS ENDPOINTS
// ==========================================

app.MapPost("/api/suggestions/inject-cluster-result", async (HttpContext httpContext, [FromBody] AnalyticsResultDto resultDto, IMongoDatabase db, IConfiguration config) =>
{
    if (!ServiceAuth.IsValid(httpContext, config))
        return Results.Unauthorized();

    var suggestionCollection = db.GetCollection<SuggestionDocument>("predictive_suggestions");

    var newSuggestion = new SuggestionDocument
    {
        UserId = resultDto.UserId,
        AssignedCluster = resultDto.ClusterName,
        RecommendationText = resultDto.TextGenerated,
        ProjectedKwhSaving = resultDto.KwhSaving,
        IsViewed = false,
        CreatedAt = DateTime.UtcNow
    };

    await suggestionCollection.InsertOneAsync(newSuggestion);
    return Results.Created($"/api/suggestions/user/{newSuggestion.UserId}", newSuggestion);
})
.WithName("InjectClusterResult");

app.MapGet("/api/suggestions/user/{userId}", async (string userId, IMongoDatabase db) =>
{
    var suggestionCollection = db.GetCollection<SuggestionDocument>("predictive_suggestions");

    var filter = Builders<SuggestionDocument>.Filter.And(
        Builders<SuggestionDocument>.Filter.Eq(s => s.UserId, userId),
        Builders<SuggestionDocument>.Filter.Eq(s => s.IsViewed, false)
    );

    var list = await suggestionCollection.Find(filter).SortByDescending(s => s.CreatedAt).ToListAsync();
    return Results.Ok(list);
})
.WithName("GetActiveSuggestions")
.RequireAuthorization();

app.MapPut("/api/suggestions/{id}/viewed", async (string id, IMongoDatabase db) =>
{
    var suggestionCollection = db.GetCollection<SuggestionDocument>("predictive_suggestions");

    var filter = Builders<SuggestionDocument>.Filter.Eq(s => s.Id, id);
    var update = Builders<SuggestionDocument>.Update.Set(s => s.IsViewed, true);

    var result = await suggestionCollection.UpdateOneAsync(filter, update);
    return result.ModifiedCount > 0
        ? Results.Ok(new { message = "Sugerencia marcada como vista." })
        : Results.NotFound(new { message = "Sugerencia no encontrada." });
})
.WithName("MarkSuggestionViewed")
.RequireAuthorization();

// ==========================================
// DEVICES ENDPOINTS
// ==========================================

app.MapPost("/api/devices/sync-mdns", async ([FromBody] List<MdnsDeviceDto> devicesDto, IMongoDatabase db) =>
{
    var deviceCollection = db.GetCollection<DeviceDocument>("devices");

    foreach (var dev in devicesDto)
    {
        var filter = Builders<DeviceDocument>.Filter.Eq(d => d.MacAddress, dev.MacAddress);
        var update = Builders<DeviceDocument>.Update
            .Set(d => d.LocalIp, dev.LocalIp)
            .Set(d => d.DeviceType, dev.DeviceType)
            .Set(d => d.UserId, dev.UserId)
            .SetOnInsert(d => d.IsOnline, true)
            .SetOnInsert(d => d.IsOn, false)
            .SetOnInsert(d => d.CreatedAt, DateTime.UtcNow);

        await deviceCollection.UpdateOneAsync(filter, update, new UpdateOptions { IsUpsert = true });
    }
    return Results.Ok(new { message = "Dispositivos Shelly sincronizados localmente." });
})
.WithName("SyncMdnsDevices")
.RequireAuthorization();

app.MapGet("/api/devices", async ([FromQuery] string? userId, IMongoDatabase db) =>
{
    var deviceCollection = db.GetCollection<DeviceDocument>("devices");

    FilterDefinition<DeviceDocument> filter;
    if (!string.IsNullOrEmpty(userId))
        filter = Builders<DeviceDocument>.Filter.Eq(d => d.UserId, userId);
    else
        filter = Builders<DeviceDocument>.Filter.Empty;

    var devices = await deviceCollection.Find(filter).ToListAsync();
    return Results.Ok(devices);
})
.WithName("GetDevices")
.RequireAuthorization();

app.MapGet("/api/devices/{id}", async (string id, IMongoDatabase db) =>
{
    var deviceCollection = db.GetCollection<DeviceDocument>("devices");

    if (!ObjectId.TryParse(id, out var objectId))
        return Results.BadRequest(new { message = "ID inválido." });

    var device = await deviceCollection.Find(d => d.Id == objectId).FirstOrDefaultAsync();
    return device is not null
        ? Results.Ok(device)
        : Results.NotFound(new { message = "Dispositivo no encontrado." });
})
.WithName("GetDeviceById")
.RequireAuthorization();

app.MapPost("/api/devices", async ([FromBody] CreateDeviceDto dto, IMongoDatabase db) =>
{
    var deviceCollection = db.GetCollection<DeviceDocument>("devices");

    var device = new DeviceDocument
    {
        UserId = dto.UserId,
        MacAddress = dto.MacAddress,
        LocalIp = dto.LocalIp,
        DeviceType = dto.DeviceType,
        DisplayName = dto.DisplayName,
        Room = dto.Room,
        Icon = dto.Icon,
        IsOnline = true,
        IsOn = false,
        CreatedAt = DateTime.UtcNow
    };

    await deviceCollection.InsertOneAsync(device);
    return Results.Created($"/api/devices/{device.Id}", device);
})
.WithName("RegisterDevice")
.RequireAuthorization();

app.MapPut("/api/devices/{id}", async (string id, [FromBody] UpdateDeviceDto dto, IMongoDatabase db) =>
{
    var deviceCollection = db.GetCollection<DeviceDocument>("devices");

    if (!ObjectId.TryParse(id, out var objectId))
        return Results.BadRequest(new { message = "ID inválido." });

    var update = Builders<DeviceDocument>.Update
        .Set(d => d.DisplayName, dto.DisplayName)
        .Set(d => d.Room, dto.Room)
        .Set(d => d.Icon, dto.Icon);

    var result = await deviceCollection.UpdateOneAsync(d => d.Id == objectId, update);
    return result.ModifiedCount > 0
        ? Results.Ok(new { message = "Dispositivo actualizado." })
        : Results.NotFound(new { message = "Dispositivo no encontrado." });
})
.WithName("UpdateDevice")
.RequireAuthorization();

app.MapDelete("/api/devices/{id}", async (string id, IMongoDatabase db) =>
{
    var deviceCollection = db.GetCollection<DeviceDocument>("devices");

    if (!ObjectId.TryParse(id, out var objectId))
        return Results.BadRequest(new { message = "ID inválido." });

    var result = await deviceCollection.DeleteOneAsync(d => d.Id == objectId);
    return result.DeletedCount > 0
        ? Results.Ok(new { message = "Dispositivo eliminado." })
        : Results.NotFound(new { message = "Dispositivo no encontrado." });
})
.WithName("DeleteDevice")
.RequireAuthorization();

app.MapPut("/api/devices/{id}/state", async (string id, [FromBody] DeviceStateDto dto, IMongoDatabase db) =>
{
    var deviceCollection = db.GetCollection<DeviceDocument>("devices");

    if (!ObjectId.TryParse(id, out var objectId))
        return Results.BadRequest(new { message = "ID inválido." });

    var update = Builders<DeviceDocument>.Update.Set(d => d.IsOn, dto.IsOn);
    var result = await deviceCollection.UpdateOneAsync(d => d.Id == objectId, update);
    return result.ModifiedCount > 0
        ? Results.Ok(new { message = $"Dispositivo {(dto.IsOn ? "encendido" : "apagado")}." })
        : Results.NotFound(new { message = "Dispositivo no encontrado." });
})
.WithName("ToggleDeviceState")
.RequireAuthorization();

app.MapPost("/api/devices/scan", async ([FromBody] ScanRequestDto dto, IMongoDatabase db) =>
{
    var scanCollection = db.GetCollection<ScanDocument>("device_scans");

    var scan = new ScanDocument
    {
        UserId = dto.UserId,
        Status = "scanning",
        DevicesFound = new List<MdnsDeviceDto>(),
        StartedAt = DateTime.UtcNow
    };

    await scanCollection.InsertOneAsync(scan);
    return Results.Accepted($"/api/devices/scan/{scan.Id}", new { scanId = scan.Id.ToString(), message = "Escaneo iniciado." });
})
.WithName("StartDeviceScan")
.RequireAuthorization();

app.MapGet("/api/devices/scan/{scanId}", async (string scanId, IMongoDatabase db) =>
{
    var scanCollection = db.GetCollection<ScanDocument>("device_scans");

    if (!ObjectId.TryParse(scanId, out var objectId))
        return Results.BadRequest(new { message = "ID inválido." });

    var scan = await scanCollection.Find(s => s.Id == objectId).FirstOrDefaultAsync();
    return scan is not null
        ? Results.Ok(scan)
        : Results.NotFound(new { message = "Escaneo no encontrado." });
})
.WithName("GetScanResults")
.RequireAuthorization();

// ==========================================
// AUTH ENDPOINTS
// ==========================================

app.MapPost("/api/auth/register", async ([FromBody] RegisterRequestDto dto, IMongoDatabase db) =>
{
    var userCollection = db.GetCollection<UserDocument>("users");

    var existing = await userCollection.Find(u => u.Email == dto.Email).FirstOrDefaultAsync();
    if (existing is not null)
        return Results.Conflict(new { message = "El correo ya está registrado." });

    var user = new UserDocument
    {
        Name = dto.Name,
        Email = dto.Email,
        PasswordHash = BCryptHelper.HashPassword(dto.Password),
        Role = "client",
        CreatedAt = DateTime.UtcNow
    };

    await userCollection.InsertOneAsync(user);
    return Results.Created($"/api/users/{user.Id}", new { user.Id, user.Name, user.Email, user.Role });
})
.WithName("RegisterUser");

app.MapPost("/api/auth/login", async ([FromBody] LoginRequestDto dto, IMongoDatabase db, IConfiguration config) =>
{
    var userCollection = db.GetCollection<UserDocument>("users");

    var user = await userCollection.Find(u => u.Email == dto.Email).FirstOrDefaultAsync();
    if (user is null || !BCryptHelper.VerifyPassword(dto.Password, user.PasswordHash))
        return Results.Unauthorized();

    var token = JwtHelper.GenerateToken(user.Id.ToString(), user.Email, user.Role, config);
    return Results.Ok(new
    {
        token,
        user = new { user.Id, user.Name, user.Email, user.Role }
    });
})
.WithName("LoginUser");

app.MapPost("/api/auth/forgot-password", async ([FromBody] ForgotPasswordDto dto, IMongoDatabase db) =>
{
    var userCollection = db.GetCollection<UserDocument>("users");

    var user = await userCollection.Find(u => u.Email == dto.Email).FirstOrDefaultAsync();
    if (user is null)
        return Results.Ok(new { message = "Si el correo existe, recibirás instrucciones para restablecer tu contraseña." });

    var resetCode = new Random().Next(100000, 999999).ToString();
    var update = Builders<UserDocument>.Update.Set(u => u.ResetCode, resetCode);
    await userCollection.UpdateOneAsync(u => u.Id == user.Id, update);

    return Results.Ok(new { message = "Si el correo existe, recibirás instrucciones para restablecer tu contraseña." });
})
.WithName("ForgotPassword");

app.MapPost("/api/auth/reset-password", async ([FromBody] ResetPasswordDto dto, IMongoDatabase db) =>
{
    var userCollection = db.GetCollection<UserDocument>("users");

    var user = await userCollection.Find(u => u.ResetCode == dto.ResetCode).FirstOrDefaultAsync();
    if (user is null)
        return Results.BadRequest(new { message = "Código de restablecimiento inválido." });

    var update = Builders<UserDocument>.Update
        .Set(u => u.PasswordHash, BCryptHelper.HashPassword(dto.NewPassword))
        .Unset(u => u.ResetCode);

    await userCollection.UpdateOneAsync(u => u.Id == user.Id, update);
    return Results.Ok(new { message = "Contraseña restablecida exitosamente." });
})
.WithName("ResetPassword");

app.MapPost("/api/auth/google", async ([FromBody] GoogleAuthDto dto, IMongoDatabase db, IConfiguration config) =>
{
    var userCollection = db.GetCollection<UserDocument>("users");

    try
    {
        var settings = new GoogleJsonWebSignature.ValidationSettings()
        {
            Audience = new[] { config["GoogleClientId"] }
        };

        var payload = await GoogleJsonWebSignature.ValidateAsync(dto.IdToken, settings);

        var user = await userCollection.Find(u => u.Email == payload.Email).FirstOrDefaultAsync();

        if (user is null)
        {
            user = new UserDocument
            {
                Name = payload.Name,
                Email = payload.Email,
                PasswordHash = "[GOOGLE_AUTH]",
                Role = "client",
                CreatedAt = DateTime.UtcNow
            };

            await userCollection.InsertOneAsync(user);
        }

        var token = JwtHelper.GenerateToken(user.Id.ToString(), user.Email, user.Role, config);

        return Results.Ok(new
        {
            token,
            user = new { user.Id, user.Name, user.Email, user.Role }
        });
    }
    catch (InvalidJwtException)
    {
        return Results.Unauthorized();
    }
})
.WithName("GoogleLogin");

// ==========================================
// USER PROFILE ENDPOINTS
// ==========================================

app.MapGet("/api/users/{id}", async (string id, HttpContext httpContext, IMongoDatabase db) =>
{
    var requesterId = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
    if (requesterId != id && !httpContext.User.IsInRole("admin"))
        return Results.Forbid();

    var userCollection = db.GetCollection<UserDocument>("users");

    var user = await userCollection.Find(u => u.Id == id).Project(u => new
    {
        u.Id,
        u.Name,
        u.Email,
        u.Phone,
        u.Role,
        u.CreatedAt
    }).FirstOrDefaultAsync();

    return user is not null
        ? Results.Ok(user)
        : Results.NotFound(new { message = "Usuario no encontrado." });
})
.WithName("GetUserProfile")
.RequireAuthorization();

app.MapPut("/api/users/{id}", async (string id, HttpContext httpContext, [FromBody] UpdateProfileDto dto, IMongoDatabase db) =>
{
    var requesterId = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
    if (requesterId != id && !httpContext.User.IsInRole("admin"))
        return Results.Forbid();

    var userCollection = db.GetCollection<UserDocument>("users");

    var updateBuilder = Builders<UserDocument>.Update.Set(u => u.Name, dto.Name);
    if (dto.Phone is not null)
        updateBuilder = updateBuilder.Set(u => u.Phone, dto.Phone);
    if (dto.Email is not null)
        updateBuilder = updateBuilder.Set(u => u.Email, dto.Email);

    var result = await userCollection.UpdateOneAsync(u => u.Id == id, updateBuilder);

    return result.ModifiedCount > 0
        ? Results.Ok(new { message = "Perfil actualizado." })
        : Results.NotFound(new { message = "Usuario no encontrado." });
})
.WithName("UpdateUserProfile")
.RequireAuthorization();

// ==========================================
// ROUTINES ENDPOINTS
// ==========================================

app.MapGet("/api/routines", async ([FromQuery] string? userId, IMongoDatabase db) =>
{
    var routineCollection = db.GetCollection<RoutineDocument>("routines");

    FilterDefinition<RoutineDocument> filter;
    if (!string.IsNullOrEmpty(userId))
        filter = Builders<RoutineDocument>.Filter.Eq(r => r.UserId, userId);
    else
        filter = Builders<RoutineDocument>.Filter.Empty;

    var routines = await routineCollection.Find(filter).SortByDescending(r => r.CreatedAt).ToListAsync();
    return Results.Ok(routines);
})
.WithName("GetRoutines")
.RequireAuthorization();

app.MapGet("/api/routines/{id}", async (string id, IMongoDatabase db) =>
{
    var routineCollection = db.GetCollection<RoutineDocument>("routines");

    if (!ObjectId.TryParse(id, out var objectId))
        return Results.BadRequest(new { message = "ID inválido." });

    var routine = await routineCollection.Find(r => r.Id == objectId).FirstOrDefaultAsync();
    return routine is not null
        ? Results.Ok(routine)
        : Results.NotFound(new { message = "Rutina no encontrada." });
})
.WithName("GetRoutineById")
.RequireAuthorization();

app.MapPost("/api/routines", async ([FromBody] CreateRoutineDto dto, IMongoDatabase db) =>
{
    var routineCollection = db.GetCollection<RoutineDocument>("routines");

    var routine = new RoutineDocument
    {
        UserId = dto.UserId,
        Name = dto.Name,
        Description = dto.Description,
        TriggerType = dto.TriggerType,
        TriggerValue = dto.TriggerValue,
        Actions = dto.Actions.Select(a => new RoutineAction
        {
            DeviceId = a.DeviceId,
            Action = a.Action,
            Value = a.Value
        }).ToList(),
        IsActive = true,
        CreatedAt = DateTime.UtcNow
    };

    await routineCollection.InsertOneAsync(routine);
    return Results.Created($"/api/routines/{routine.Id}", routine);
})
.WithName("CreateRoutine")
.RequireAuthorization();

app.MapPut("/api/routines/{id}", async (string id, [FromBody] UpdateRoutineDto dto, IMongoDatabase db) =>
{
    var routineCollection = db.GetCollection<RoutineDocument>("routines");

    if (!ObjectId.TryParse(id, out var objectId))
        return Results.BadRequest(new { message = "ID inválido." });

    var update = Builders<RoutineDocument>.Update
        .Set(r => r.Name, dto.Name)
        .Set(r => r.Description, dto.Description)
        .Set(r => r.IsActive, dto.IsActive);

    var result = await routineCollection.UpdateOneAsync(r => r.Id == objectId, update);
    return result.ModifiedCount > 0
        ? Results.Ok(new { message = "Rutina actualizada." })
        : Results.NotFound(new { message = "Rutina no encontrada." });
})
.WithName("UpdateRoutine")
.RequireAuthorization();

app.MapDelete("/api/routines/{id}", async (string id, IMongoDatabase db) =>
{
    var routineCollection = db.GetCollection<RoutineDocument>("routines");

    if (!ObjectId.TryParse(id, out var objectId))
        return Results.BadRequest(new { message = "ID inválido." });

    var result = await routineCollection.DeleteOneAsync(r => r.Id == objectId);
    return result.DeletedCount > 0
        ? Results.Ok(new { message = "Rutina eliminada." })
        : Results.NotFound(new { message = "Rutina no encontrada." });
})
.WithName("DeleteRoutine")
.RequireAuthorization();

app.MapPost("/api/routines/{id}/execute", async (string id, IMongoDatabase db) =>
{
    var routineCollection = db.GetCollection<RoutineDocument>("routines");

    if (!ObjectId.TryParse(id, out var objectId))
        return Results.BadRequest(new { message = "ID inválido." });

    var routine = await routineCollection.Find(r => r.Id == objectId).FirstOrDefaultAsync();
    if (routine is null)
        return Results.NotFound(new { message = "Rutina no encontrada." });

    return Results.Ok(new { message = $"Rutina '{routine.Name}' ejecutada.", actions = routine.Actions });
})
.WithName("ExecuteRoutine")
.RequireAuthorization();

// ==========================================
// GESTURE ENDPOINTS
// ==========================================

app.MapGet("/api/gestures/catalog", async (IMongoDatabase db) =>
{
    var catalogCollection = db.GetCollection<GestureCatalogDocument>("gesture_catalog");
    var catalogo = await catalogCollection.Find(Builders<GestureCatalogDocument>.Filter.Empty)
        .SortBy(g => g.Label)
        .ToListAsync();
    return Results.Ok(catalogo);
})
.WithName("GetGestureCatalog")
.RequireAuthorization();

app.MapGet("/api/gestures", async ([FromQuery] string? userId, IMongoDatabase db) =>
{
    var gestureCollection = db.GetCollection<GestureDocument>("gestures");

    FilterDefinition<GestureDocument> filter;
    if (!string.IsNullOrEmpty(userId))
        filter = Builders<GestureDocument>.Filter.Eq(g => g.UserId, userId);
    else
        filter = Builders<GestureDocument>.Filter.Empty;

    var gestures = await gestureCollection.Find(filter).ToListAsync();
    return Results.Ok(gestures);
})
.WithName("GetGestures")
.RequireAuthorization();

app.MapPost("/api/gestures", async ([FromBody] CreateGestureDto dto, IMongoDatabase db) =>
{
    var gestureCollection = db.GetCollection<GestureDocument>("gestures");

    var gesture = new GestureDocument
    {
        UserId = dto.UserId,
        Name = dto.Name,
        GestureData = dto.GestureData,
        LinkedDeviceId = dto.LinkedDeviceId,
        LinkedAction = dto.LinkedAction,
        CreatedAt = DateTime.UtcNow
    };

    await gestureCollection.InsertOneAsync(gesture);
    return Results.Created($"/api/gestures/{gesture.Id}", gesture);
})
.WithName("CreateGesture")
.RequireAuthorization();

app.MapPut("/api/gestures/{id}", async (string id, [FromBody] UpdateGestureDto dto, IMongoDatabase db) =>
{
    var gestureCollection = db.GetCollection<GestureDocument>("gestures");

    if (!ObjectId.TryParse(id, out var objectId))
        return Results.BadRequest(new { message = "ID inválido." });

    var update = Builders<GestureDocument>.Update
        .Set(g => g.Name, dto.Name)
        .Set(g => g.LinkedDeviceId, dto.LinkedDeviceId)
        .Set(g => g.LinkedAction, dto.LinkedAction);

    var result = await gestureCollection.UpdateOneAsync(g => g.Id == objectId, update);
    return result.ModifiedCount > 0
        ? Results.Ok(new { message = "Gesto actualizado." })
        : Results.NotFound(new { message = "Gesto no encontrado." });
})
.WithName("UpdateGesture")
.RequireAuthorization();

app.MapDelete("/api/gestures/{id}", async (string id, IMongoDatabase db) =>
{
    var gestureCollection = db.GetCollection<GestureDocument>("gestures");

    if (!ObjectId.TryParse(id, out var objectId))
        return Results.BadRequest(new { message = "ID inválido." });

    var result = await gestureCollection.DeleteOneAsync(g => g.Id == objectId);
    return result.DeletedCount > 0
        ? Results.Ok(new { message = "Gesto eliminado." })
        : Results.NotFound(new { message = "Gesto no encontrado." });
})
.WithName("DeleteGesture")
.RequireAuthorization();

app.MapPut("/api/gestures/{id}/link", async (string id, [FromBody] LinkGestureDto dto, IMongoDatabase db) =>
{
    var gestureCollection = db.GetCollection<GestureDocument>("gestures");

    if (!ObjectId.TryParse(id, out var objectId))
        return Results.BadRequest(new { message = "ID inválido." });

    var update = Builders<GestureDocument>.Update
        .Set(g => g.LinkedDeviceId, dto.DeviceId)
        .Set(g => g.LinkedAction, dto.Action);

    var result = await gestureCollection.UpdateOneAsync(g => g.Id == objectId, update);
    return result.ModifiedCount > 0
        ? Results.Ok(new { message = "Gesto vinculado al dispositivo." })
        : Results.NotFound(new { message = "Gesto no encontrado." });
})
.WithName("LinkGesture")
.RequireAuthorization();

// ==========================================
// ADMIN ENDPOINTS
// ==========================================

app.MapGet("/api/admin/dashboard/metrics", async (IMongoDatabase db) =>
{
    var userCollection = db.GetCollection<UserDocument>("users");
    var deviceCollection = db.GetCollection<DeviceDocument>("devices");
    var routineCollection = db.GetCollection<RoutineDocument>("routines");

    var totalUsers = await userCollection.CountDocumentsAsync(FilterDefinition<UserDocument>.Empty);
    var totalDevices = await deviceCollection.CountDocumentsAsync(FilterDefinition<DeviceDocument>.Empty);
    var activeRoutines = await routineCollection.CountDocumentsAsync(
        Builders<RoutineDocument>.Filter.Eq(r => r.IsActive, true)
    );
    var onlineDevices = await deviceCollection.CountDocumentsAsync(
        Builders<DeviceDocument>.Filter.Eq(d => d.IsOnline, true)
    );

    return Results.Ok(new
    {
        totalUsers,
        totalDevices,
        activeRoutines,
        onlineDevices,
        generatedAt = DateTime.UtcNow
    });
})
.WithName("GetAdminDashboardMetrics")
.RequireAuthorization("AdminOnly");

app.MapGet("/api/admin/users", async ([FromQuery] int? page, [FromQuery] int? limit, IMongoDatabase db) =>
{
    var userCollection = db.GetCollection<UserDocument>("users");

    var pageVal = page ?? 1;
    var limitVal = limit ?? 20;

    var total = await userCollection.CountDocumentsAsync(FilterDefinition<UserDocument>.Empty);
    var users = await userCollection.Find(FilterDefinition<UserDocument>.Empty)
        .SortByDescending(u => u.CreatedAt)
        .Skip((pageVal - 1) * limitVal)
        .Limit(limitVal)
        .Project(u => new { u.Id, u.Name, u.Email, u.Role, u.CreatedAt })
        .ToListAsync();

    return Results.Ok(new { total, page = pageVal, limit = limitVal, data = users });
})
.WithName("AdminGetUsers")
.RequireAuthorization("AdminOnly");

app.MapGet("/api/admin/devices", async ([FromQuery] int? page, [FromQuery] int? limit, IMongoDatabase db) =>
{
    var deviceCollection = db.GetCollection<DeviceDocument>("devices");

    var pageVal = page ?? 1;
    var limitVal = limit ?? 20;

    var total = await deviceCollection.CountDocumentsAsync(FilterDefinition<DeviceDocument>.Empty);
    var devices = await deviceCollection.Find(FilterDefinition<DeviceDocument>.Empty)
        .SortByDescending(d => d.CreatedAt)
        .Skip((pageVal - 1) * limitVal)
        .Limit(limitVal)
        .ToListAsync();

    return Results.Ok(new { total, page = pageVal, limit = limitVal, data = devices });
})
.WithName("AdminGetDevices")
.RequireAuthorization("AdminOnly");

app.MapGet("/api/admin/routines", async ([FromQuery] int? page, [FromQuery] int? limit, IMongoDatabase db) =>
{
    var routineCollection = db.GetCollection<RoutineDocument>("routines");

    var pageVal = page ?? 1;
    var limitVal = limit ?? 20;

    var total = await routineCollection.CountDocumentsAsync(FilterDefinition<RoutineDocument>.Empty);
    var routines = await routineCollection.Find(FilterDefinition<RoutineDocument>.Empty)
        .SortByDescending(r => r.CreatedAt)
        .Skip((pageVal - 1) * limitVal)
        .Limit(limitVal)
        .ToListAsync();

    return Results.Ok(new { total, page = pageVal, limit = limitVal, data = routines });
})
.WithName("AdminGetRoutines")
.RequireAuthorization("AdminOnly");

app.MapGet("/api/admin/raspberries", async (IMongoDatabase db) =>
{
    var raspberryCollection = db.GetCollection<RaspberryDocument>("raspberries");

    var raspberries = await raspberryCollection.Find(FilterDefinition<RaspberryDocument>.Empty).ToListAsync();
    return Results.Ok(raspberries);
})
.WithName("AdminGetRaspberries")
.RequireAuthorization("AdminOnly");

// ==========================================
// ANALYTICS / CONSUMPTION ENDPOINTS
// ==========================================

app.MapGet("/api/analytics/consumption/summary", async ([FromQuery] string userId, [FromQuery] string? period, IMongoDatabase db) =>
{
    var sessionCollection = db.GetCollection<UserSessionDocument>("user_sessions");

    var daysBack = period switch
    {
        "week" => 7,
        "month" => 30,
        "year" => 365,
        _ => 7
    };

    var since = DateTime.UtcNow.AddDays(-daysBack).ToString("yyyy-MM-dd");
    var filter = Builders<UserSessionDocument>.Filter.And(
        Builders<UserSessionDocument>.Filter.Eq(s => s.UserId, userId),
        Builders<UserSessionDocument>.Filter.Gte(s => s.DateString, since)
    );

    var sessions = await sessionCollection.Find(filter).ToListAsync();
    var totalKwh = sessions.Sum(s => s.DeviceHistory.Sum(d => d.KwhConsumed));
    var totalEvents = sessions.Sum(s => s.DeviceHistory.Count);

    return Results.Ok(new
    {
        userId,
        period,
        totalKwh,
        totalEvents,
        deviceCount = sessions.SelectMany(s => s.DeviceHistory).Select(d => d.DeviceId).Distinct().Count(),
        sessionsCount = sessions.Count
    });
})
.WithName("GetConsumptionSummary")
.RequireAuthorization();

// ==========================================
// CLIENT DASHBOARD ENDPOINTS
// ==========================================

app.MapGet("/api/client/dashboard/{userId}", async (string userId, IMongoDatabase db) =>
{
    var deviceCollection = db.GetCollection<DeviceDocument>("devices");
    var routineCollection = db.GetCollection<RoutineDocument>("routines");
    var suggestionCollection = db.GetCollection<SuggestionDocument>("predictive_suggestions");

    var devicesTask = deviceCollection.Find(d => d.UserId == userId).ToListAsync();
    var routinesTask = routineCollection.Find(r => r.UserId == userId && r.IsActive).ToListAsync();
    var suggestionsTask = suggestionCollection.Find(s => s.UserId == userId && !s.IsViewed)
        .SortByDescending(s => s.CreatedAt).Limit(5).ToListAsync();

    await Task.WhenAll(devicesTask, routinesTask, suggestionsTask);

    var devices = devicesTask.Result;
    var routines = routinesTask.Result;
    var suggestions = suggestionsTask.Result;

    return Results.Ok(new
    {
        totalDevices = devices.Count,
        devicesOn = devices.Count(d => d.IsOn),
        devicesOnline = devices.Count(d => d.IsOnline),
        activeRoutines = routines.Count,
        unreadSuggestions = suggestions.Count,
        devices,
        routines,
        suggestions
    });
})
.WithName("GetClientDashboard")
.RequireAuthorization();

// ==========================================
// INTEGRATIONS ENDPOINTS
// ==========================================

app.MapPost("/api/integrations/spotify/token", async ([FromBody] SpotifyTokenDto dto, IMongoDatabase db) =>
{
    var integrationCollection = db.GetCollection<BsonDocument>("integrations");

    var filter = Builders<BsonDocument>.Filter.And(
        Builders<BsonDocument>.Filter.Eq("UserId", dto.UserId),
        Builders<BsonDocument>.Filter.Eq("Service", "spotify")
    );

    var update = Builders<BsonDocument>.Update
        .Set("AccessToken", dto.AccessToken)
        .Set("RefreshToken", dto.RefreshToken)
        .Set("ExpiresAt", dto.ExpiresAt);

    await integrationCollection.UpdateOneAsync(filter, update, new UpdateOptions { IsUpsert = true });
    return Results.Ok(new { message = "Token de Spotify almacenado." });
})
.WithName("StoreSpotifyToken")
.RequireAuthorization();

// ==========================================
// HEALTH CHECK
// ==========================================

app.MapGet("/api/health", async (IMongoDatabase db) =>
{
    try
    {
        await db.RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1));
        return Results.Ok(new { status = "healthy", database = "connected", timestamp = DateTime.UtcNow });
    }
    catch
    {
        return Results.StatusCode(503);
    }
})
.WithName("HealthCheck");

// ==========================================
// ADMIN SUMMARY ENDPOINT
// ==========================================

app.MapGet("/api/admin/summary", async (IMongoDatabase db) =>
{
    var userCollection = db.GetCollection<UserDocument>("users");
    var deviceCollection = db.GetCollection<DeviceDocument>("devices");
    var raspberryCollection = db.GetCollection<RaspberryDocument>("raspberries");

    var usuariosActivos = await userCollection.CountDocumentsAsync(FilterDefinition<UserDocument>.Empty);
    var dispositivosRegistrados = await deviceCollection.CountDocumentsAsync(FilterDefinition<DeviceDocument>.Empty);
    var alertasPendientes = await raspberryCollection.CountDocumentsAsync(
        Builders<RaspberryDocument>.Filter.Ne(r => r.Status, "online")
    );

    return Results.Ok(new
    {
        usuariosActivos,
        dispositivosRegistrados,
        alertasPendientes
    });
})
.WithName("GetAdminSummary")
.RequireAuthorization("AdminOnly");

// ==========================================
// ANALYTICS DEVICE-USAGE ENDPOINT
// ==========================================

app.MapGet("/api/analytics/device-usage", async ([FromQuery] string? userId, [FromQuery] string? period, IMongoDatabase db) =>
{
    var sessionCollection = db.GetCollection<UserSessionDocument>("user_sessions");

    var daysBack = period switch
    {
        "week" => 7,
        "month" => 30,
        "year" => 365,
        _ => 7
    };

    var since = DateTime.UtcNow.AddDays(-daysBack).ToString("yyyy-MM-dd");

    FilterDefinition<UserSessionDocument> filter;
    if (!string.IsNullOrEmpty(userId))
        filter = Builders<UserSessionDocument>.Filter.And(
            Builders<UserSessionDocument>.Filter.Eq(s => s.UserId, userId),
            Builders<UserSessionDocument>.Filter.Gte(s => s.DateString, since)
        );
    else
        filter = Builders<UserSessionDocument>.Filter.Gte(s => s.DateString, since);

    var sessions = await sessionCollection.Find(filter).SortBy(s => s.DateString).ToListAsync();

    var dailyGroups = sessions.GroupBy(s => s.DateString).OrderBy(g => g.Key);

    var dates = new List<string>();
    var frequencies = new List<int>();
    var kwhData = new List<double>();

    foreach (var group in dailyGroups)
    {
        dates.Add(group.Key);
        var totalEvents = group.Sum(s => s.DeviceHistory.Count);
        var totalKwh = group.Sum(s => s.DeviceHistory.Sum(d => d.KwhConsumed));
        frequencies.Add(totalEvents);
        kwhData.Add(Math.Round(totalKwh, 2));
    }

    var allLogs = sessions.SelectMany(s => s.DeviceHistory).ToList();
    var hourlyGroups = allLogs.GroupBy(d => d.Timestamp.Hour).OrderBy(g => g.Key);

    var hours = new List<string>();
    var hourlyFrequencies = new List<int>();

    for (int h = 0; h < 24; h++)
    {
        hours.Add(h.ToString("D2"));
        var match = hourlyGroups.FirstOrDefault(g => g.Key == h);
        hourlyFrequencies.Add(match?.Count() ?? 0);
    }

    var deviceTypes = allLogs
        .GroupBy(d => d.DeviceType)
        .Select(g => new { name = g.Key, count = g.Count() })
        .ToList();

    return Results.Ok(new
    {
        dates,
        frequencies,
        kwhData,
        hours,
        hourlyFrequencies,
        deviceTypes,
        totalSessions = sessions.Count,
        totalEvents = allLogs.Count,
        period = period ?? "week"
    });
})
.WithName("GetDeviceUsage")
.RequireAuthorization();

// ==========================================
// RASPBERRY MONITOR ENDPOINT
// ==========================================

app.MapGet("/api/monitor/raspberries", async (IMongoDatabase db) =>
{
    var raspberryCollection = db.GetCollection<RaspberryDocument>("raspberries");

    var raspberries = await raspberryCollection.Find(FilterDefinition<RaspberryDocument>.Empty)
        .SortByDescending(r => r.LastSeen)
        .ToListAsync();

    var result = raspberries.Select(r => new
    {
        id = r.Id.ToString(),
        userId = r.UserId ?? "unknown",
        name = r.Name,
        localIp = r.LocalIp,
        contact = r.Contact ?? "",
        status = r.Status == "online" ? "Online" : "Offline",
        cpuUsage = r.CpuUsage,
        ramUsage = r.RamUsage,
        storageUsage = r.StorageUsage,
        uptime = r.Uptime,
        lastSeen = r.LastSeen
    });

    return Results.Ok(result);
})
.WithName("GetMonitorRaspberries")
.RequireAuthorization("AdminOnly");

// ==========================================
// USER PROFILE ENDPOINTS (JWT-based)
// ==========================================

app.MapGet("/api/users/profile", async (HttpContext httpContext, IMongoDatabase db) =>
{
    var userId = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
    if (userId is null)
        return Results.Unauthorized();

    var userCollection = db.GetCollection<UserDocument>("users");

    if (!ObjectId.TryParse(userId, out var objectId))
        return Results.BadRequest(new { message = "ID de usuario inválido." });

    var user = await userCollection.Find(u => u.Id == userId)
        .Project(u => new
        {
            u.Id,
            u.Name,
            u.Email,
            u.Phone,
            u.Role,
            u.CreatedAt
        })
        .FirstOrDefaultAsync();

    return user is not null
        ? Results.Ok(user)
        : Results.NotFound(new { message = "Usuario no encontrado." });
})
.WithName("GetMyProfile")
.RequireAuthorization();

app.MapPut("/api/users/profile", async (HttpContext httpContext, [FromBody] UpdateProfileDto dto, IMongoDatabase db) =>
{
    var userId = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
    if (userId is null)
        return Results.Unauthorized();

    var userCollection = db.GetCollection<UserDocument>("users");

    if (!ObjectId.TryParse(userId, out var objectId))
        return Results.BadRequest(new { message = "ID de usuario inválido." });

    var updateBuilder = Builders<UserDocument>.Update.Set(u => u.Name, dto.Name);
    if (dto.Phone is not null)
        updateBuilder = updateBuilder.Set(u => u.Phone, dto.Phone);
    if (dto.Email is not null)
        updateBuilder = updateBuilder.Set(u => u.Email, dto.Email);

    var result = await userCollection.UpdateOneAsync(u => u.Id == userId, updateBuilder);
    return result.ModifiedCount > 0
        ? Results.Ok(new { message = "Perfil actualizado exitosamente." })
        : Results.NotFound(new { message = "Usuario no encontrado." });
})
.WithName("UpdateMyProfile")
.RequireAuthorization();

app.MapDelete("/api/users/profile", async (HttpContext httpContext, IMongoDatabase db) =>
{
    var userId = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
    if (userId is null)
        return Results.Unauthorized();

    var userCollection = db.GetCollection<UserDocument>("users");

    if (!ObjectId.TryParse(userId, out var objectId))
        return Results.BadRequest(new { message = "ID de usuario inválido." });

    var result = await userCollection.DeleteOneAsync(u => u.Id == userId);
    return result.DeletedCount > 0
        ? Results.Ok(new { message = "Cuenta eliminada exitosamente." })
        : Results.NotFound(new { message = "Usuario no encontrado." });
})
.WithName("DeleteMyProfile")
.RequireAuthorization();

// ==========================================
// USER DEVICES ENDPOINT (JWT-based)
// ==========================================

app.MapGet("/api/users/devices", async (HttpContext httpContext, IMongoDatabase db) =>
{
    var userId = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
    if (userId is null)
        return Results.Unauthorized();

    var deviceCollection = db.GetCollection<DeviceDocument>("devices");

    var devices = await deviceCollection.Find(d => d.UserId == userId).ToListAsync();

    return Results.Ok(new
    {
        total = devices.Count,
        devicesOnline = devices.Count(d => d.IsOnline),
        devicesOn = devices.Count(d => d.IsOn),
        data = devices.Select(d => new
        {
            d.Id,
            d.UserId,
            d.MacAddress,
            d.LocalIp,
            d.DeviceType,
            d.DisplayName,
            d.Room,
            d.Icon,
            d.IsOnline,
            d.IsOn,
            d.CreatedAt
        })
    });
})
.WithName("GetMyDevices")
.RequireAuthorization();


// ==========================================
// STORE / VENTAS / PRODUCTOS ENDPOINTS
// ==========================================

app.MapGet("/api/productos", async (IMongoDatabase db) =>
{
    var productoCollection = db.GetCollection<ProductoDocument>("productos");
    // Usamos Project para omitir ImagenBase64 en el listado y no saturar la red
    var productos = await productoCollection.Find(FilterDefinition<ProductoDocument>.Empty)
        .SortByDescending(p => p.CreatedAt)
        .Project(p => new
        {
            p.Id,
            p.Nombre,
            p.Descripcion,
            p.Receta,
            p.PorcentajeUtilidad,
            p.PrecioBruto,
            p.PrecioFinal,
            p.Stock,
            p.Documentos,
            p.CreatedAt,
            TieneImagen = p.ImagenBase64 != null
        })
        .ToListAsync();
    return Results.Ok(productos);
})
.WithName("GetProductos");

app.MapGet("/api/productos/{id}", async (string id, IMongoDatabase db) =>
{
    if (!ObjectId.TryParse(id, out _))
        return Results.BadRequest(new { message = "ID inválido." });

    var productoCollection = db.GetCollection<ProductoDocument>("productos");

    // Aquí sí devolvemos el objeto completo incluyendo la ImagenBase64
    var producto = await productoCollection.Find(p => p.Id == id).FirstOrDefaultAsync();
    return producto is not null
        ? Results.Ok(producto)
        : Results.NotFound(new { message = "Producto no encontrado." });
})
.WithName("GetProductoById");

app.MapPost("/api/productos", async ([FromBody] SaveProductoDto dto, IMongoDatabase db) =>
{
    var validationError = ProductoValidator.Validate(dto);
    if (validationError is not null)
        return Results.BadRequest(new { message = validationError });

    var materiaPrimaCollection = db.GetCollection<MateriaPrimaDocument>("materias_primas");
    var (receta, precioBruto, recetaError) = await RecetaBuilder.Construir(dto.Receta, materiaPrimaCollection);
    if (recetaError is not null)
        return Results.BadRequest(new { message = recetaError });

    var porcentajeUtilidad = dto.PorcentajeUtilidad ?? 20;
    var precioFinal = Math.Round(precioBruto * (1 + (porcentajeUtilidad / 100)), 2);

    var productoCollection = db.GetCollection<ProductoDocument>("productos");

    var producto = new ProductoDocument
    {
        Nombre = dto.Nombre.Trim(),
        Descripcion = dto.Descripcion?.Trim() ?? string.Empty,
        PorcentajeUtilidad = porcentajeUtilidad,
        PrecioBruto = Math.Round(precioBruto, 2),
        PrecioFinal = precioFinal,
        Stock = dto.Stock,
        ImagenBase64 = dto.ImagenBase64,
        Receta = receta,
        Documentos = RecetaBuilder.ConstruirDocumentos(dto.Documentos),
        CreatedAt = DateTime.UtcNow
    };

    await productoCollection.InsertOneAsync(producto);
    return Results.Created($"/api/productos/{producto.Id}", producto);
})
.WithName("CreateProducto")
.RequireAuthorization("AdminOnly");

app.MapPut("/api/productos/{id}", async (string id, [FromBody] SaveProductoDto dto, IMongoDatabase db) =>
{
    if (!ObjectId.TryParse(id, out _))
        return Results.BadRequest(new { message = "ID inválido." });

    var validationError = ProductoValidator.Validate(dto);
    if (validationError is not null)
        return Results.BadRequest(new { message = validationError });

    var materiaPrimaCollection = db.GetCollection<MateriaPrimaDocument>("materias_primas");
    var (receta, precioBruto, recetaError) = await RecetaBuilder.Construir(dto.Receta, materiaPrimaCollection);
    if (recetaError is not null)
        return Results.BadRequest(new { message = recetaError });

    var porcentajeUtilidad = dto.PorcentajeUtilidad ?? 20;
    var precioFinal = Math.Round(precioBruto * (1 + (porcentajeUtilidad / 100)), 2);

    var productoCollection = db.GetCollection<ProductoDocument>("productos");

    var updateBuilder = Builders<ProductoDocument>.Update
        .Set(p => p.Nombre, dto.Nombre.Trim())
        .Set(p => p.Descripcion, dto.Descripcion?.Trim() ?? string.Empty)
        .Set(p => p.PorcentajeUtilidad, porcentajeUtilidad)
        .Set(p => p.PrecioBruto, Math.Round(precioBruto, 2))
        .Set(p => p.PrecioFinal, precioFinal)
        .Set(p => p.Stock, dto.Stock)
        .Set(p => p.Receta, receta)
        .Set(p => p.Documentos, RecetaBuilder.ConstruirDocumentos(dto.Documentos));

    if (dto.ImagenBase64 is not null)
        updateBuilder = updateBuilder.Set(p => p.ImagenBase64, dto.ImagenBase64);

    var result = await productoCollection.UpdateOneAsync(p => p.Id == id, updateBuilder);
    return result.MatchedCount > 0
        ? Results.Ok(new { message = "Producto actualizado.", precioBruto = Math.Round(precioBruto, 2), precioFinal })
        : Results.NotFound(new { message = "Producto no encontrado." });
})
.WithName("UpdateProducto")
.RequireAuthorization("AdminOnly");

// NUEVO ENDPOINT: Recalcula precio usando Costos Promedios Actuales de las Materias Primas
app.MapPost("/api/productos/{id}/recalcular", async (string id, IMongoDatabase db) =>
{
    if (!ObjectId.TryParse(id, out _))
        return Results.BadRequest(new { message = "ID inválido." });

    var productoCollection = db.GetCollection<ProductoDocument>("productos");
    var materiaPrimaCollection = db.GetCollection<MateriaPrimaDocument>("materias_primas");

    var producto = await productoCollection.Find(p => p.Id == id).FirstOrDefaultAsync();
    if (producto is null) return Results.NotFound(new { message = "Producto no encontrado." });

    var recetaDto = producto.Receta.Select(r => new RecetaItemDto(r.MateriaPrimaId, r.Cantidad)).ToList();

    var (nuevaReceta, precioBruto, error) = await RecetaBuilder.Construir(recetaDto, materiaPrimaCollection);
    if (error is not null) return Results.BadRequest(new { message = error });

    var precioFinal = Math.Round(precioBruto * (1 + (producto.PorcentajeUtilidad / 100)), 2);

    var update = Builders<ProductoDocument>.Update
        .Set(p => p.Receta, nuevaReceta)
        .Set(p => p.PrecioBruto, Math.Round(precioBruto, 2))
        .Set(p => p.PrecioFinal, precioFinal);

    await productoCollection.UpdateOneAsync(p => p.Id == id, update);

    return Results.Ok(new
    {
        productoId = id,
        precioBruto = Math.Round(precioBruto, 2),
        precioFinal,
        porcentajeUtilidad = producto.PorcentajeUtilidad
    });
})
.WithName("RecalcularPrecioProducto")
.RequireAuthorization("AdminOnly");

app.MapDelete("/api/productos/{id}", async (string id, IMongoDatabase db) =>
{
    if (!ObjectId.TryParse(id, out _))
        return Results.BadRequest(new { message = "ID inválido." });

    var productoCollection = db.GetCollection<ProductoDocument>("productos");

    var result = await productoCollection.DeleteOneAsync(p => p.Id == id);
    return result.DeletedCount > 0
        ? Results.Ok(new { message = "Producto eliminado." })
        : Results.NotFound(new { message = "Producto no encontrado." });
})
.WithName("DeleteProducto")
.RequireAuthorization("AdminOnly");

app.MapPost("/api/ventas", async (HttpContext httpContext, [FromBody] CreateVentaDto dto, IMongoDatabase db, IMongoClient mongoClient) =>
{
    var userId = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
    if (userId is null) return Results.Unauthorized();

    if (dto.Items is null || dto.Items.Count == 0)
        return Results.BadRequest(new { message = "La venta debe incluir al menos un producto." });

    if (dto.Items.Any(i => i.Cantidad <= 0))
        return Results.BadRequest(new { message = "Cada cantidad debe ser mayor a cero." });

    var productoCollection = db.GetCollection<ProductoDocument>("productos");
    var ventasCollection = db.GetCollection<VentaDocument>("ventas");

    var cantidadesPorProducto = dto.Items
        .GroupBy(i => i.ProductoId)
        .ToDictionary(g => g.Key, g => g.Sum(i => i.Cantidad));

    if (cantidadesPorProducto.Keys.Any(id => !ObjectId.TryParse(id, out _)))
        return Results.BadRequest(new { message = "La venta incluye un producto con ID inválido." });

    using var session = await mongoClient.StartSessionAsync();

    try
    {
        var venta = await session.WithTransactionAsync(async (s, ct) =>
        {
            var items = new List<VentaItem>();

            foreach (var (productoId, cantidad) in cantidadesPorProducto)
            {
                var producto = await productoCollection.Find(s, p => p.Id == productoId).FirstOrDefaultAsync(ct);
                if (producto is null)
                    throw new VentaInvalidaException("Uno de los productos ya no está disponible.");

                var stockFilter = Builders<ProductoDocument>.Filter.And(
                    Builders<ProductoDocument>.Filter.Eq(p => p.Id, productoId),
                    Builders<ProductoDocument>.Filter.Gte(p => p.Stock, cantidad));

                var stockUpdate = Builders<ProductoDocument>.Update.Inc(p => p.Stock, -cantidad);

                var stockResult = await productoCollection.UpdateOneAsync(s, stockFilter, stockUpdate, cancellationToken: ct);
                if (stockResult.ModifiedCount == 0)
                    throw new VentaInvalidaException($"Stock insuficiente para {producto.Nombre}.");

                items.Add(new VentaItem
                {
                    ProductoId = productoId,
                    NombreProducto = producto.Nombre,
                    Cantidad = cantidad,
                    PrecioUnitario = producto.PrecioFinal 
                });
            }

            var nuevaVenta = new VentaDocument
            {
                UserId = userId,
                Items = items,
                Total = items.Sum(i => i.Cantidad * i.PrecioUnitario),
                FechaVenta = DateTime.UtcNow
            };

            await ventasCollection.InsertOneAsync(s, nuevaVenta, cancellationToken: ct);
            return nuevaVenta;
        });

        return Results.Created($"/api/ventas/{venta.Id}", venta);
    }
    catch (VentaInvalidaException ex)
    {
        return Results.BadRequest(new { message = ex.Message });
    }
})
.WithName("CreateVenta")
.RequireAuthorization();

app.MapGet("/api/ventas/mis-ventas", async (HttpContext httpContext, IMongoDatabase db) =>
{
    var userId = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
    if (userId is null) return Results.Unauthorized();

    var ventasCollection = db.GetCollection<VentaDocument>("ventas");

    var misVentas = await ventasCollection.Find(v => v.UserId == userId)
        .SortByDescending(v => v.FechaVenta)
        .ToListAsync();

    return Results.Ok(misVentas);
})
.WithName("GetMisVentas")
.RequireAuthorization();

app.MapGet("/api/ventas", async (IMongoDatabase db) =>
{
    var ventasCollection = db.GetCollection<VentaDocument>("ventas");
    var todasLasVentas = await ventasCollection.Find(FilterDefinition<VentaDocument>.Empty)
        .SortByDescending(v => v.FechaVenta)
        .ToListAsync();

    return Results.Ok(todasLasVentas);
})
.WithName("GetAllVentas")
.RequireAuthorization("AdminOnly");

// ==========================================
// PRODUCCION ENDPOINTS
// ==========================================

app.MapGet("/api/produccion", async ([FromQuery] string? estado, IMongoDatabase db) =>
{
    var produccionCollection = db.GetCollection<ProduccionDocument>("producciones");

    FilterDefinition<ProduccionDocument> filter;
    if (!string.IsNullOrEmpty(estado))
        filter = Builders<ProduccionDocument>.Filter.Eq(p => p.Estado, estado);
    else
        filter = Builders<ProduccionDocument>.Filter.Empty;

    var lotes = await produccionCollection.Find(filter)
        .SortByDescending(p => p.CreatedAt)
        .ToListAsync();

    return Results.Ok(lotes);
})
.WithName("GetProduccion")
.RequireAuthorization("AdminOnly");

app.MapGet("/api/produccion/{id}", async (string id, IMongoDatabase db) =>
{
    if (!ObjectId.TryParse(id, out _))
        return Results.BadRequest(new { message = "ID inválido." });

    var produccionCollection = db.GetCollection<ProduccionDocument>("producciones");

    var lote = await produccionCollection.Find(p => p.Id == id).FirstOrDefaultAsync();
    return lote is not null
        ? Results.Ok(lote)
        : Results.NotFound(new { message = "Lote de producción no encontrado." });
})
.WithName("GetProduccionById")
.RequireAuthorization("AdminOnly");

app.MapPost("/api/produccion", async ([FromBody] CreateProduccionDto dto, IMongoDatabase db) =>
{
    if (dto.CantidadPlaneada <= 0)
        return Results.BadRequest(new { message = "La cantidad planeada debe ser mayor a cero." });

    if (!ObjectId.TryParse(dto.ProductoId, out _))
        return Results.BadRequest(new { message = "ID de producto inválido." });

    var productoCollection = db.GetCollection<ProductoDocument>("productos");
    var producto = await productoCollection.Find(p => p.Id == dto.ProductoId).FirstOrDefaultAsync();
    if (producto is null)
        return Results.BadRequest(new { message = "El producto no existe en el catálogo." });

    var produccionCollection = db.GetCollection<ProduccionDocument>("producciones");

    var lote = new ProduccionDocument
    {
        ProductoId = dto.ProductoId,
        ProductoNombre = producto.Nombre,
        CantidadPlaneada = dto.CantidadPlaneada,
        CantidadProducida = 0,
        Estado = ProduccionEstados.Planificado,
        Notas = dto.Notas,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    await produccionCollection.InsertOneAsync(lote);
    return Results.Created($"/api/produccion/{lote.Id}", lote);
})
.WithName("CreateProduccion")
.RequireAuthorization("AdminOnly");

app.MapPut("/api/produccion/{id}", async (string id, [FromBody] UpdateProduccionDto dto, IMongoDatabase db) =>
{
    if (!ObjectId.TryParse(id, out _))
        return Results.BadRequest(new { message = "ID inválido." });

    if (!ProduccionEstados.EsValido(dto.Estado))
        return Results.BadRequest(new { message = "Estado de producción inválido." });

    if (dto.CantidadProducida < 0)
        return Results.BadRequest(new { message = "La cantidad producida no puede ser negativa." });

    if (dto.Estado == ProduccionEstados.Completado && dto.CantidadProducida == 0)
        return Results.BadRequest(new { message = "No se puede completar un lote sin unidades producidas." });

    var produccionCollection = db.GetCollection<ProduccionDocument>("producciones");

    var lote = await produccionCollection.Find(p => p.Id == id).FirstOrDefaultAsync();
    if (lote is null)
        return Results.NotFound(new { message = "Lote de producción no encontrado." });

    if (lote.Estado == ProduccionEstados.Completado || lote.Estado == ProduccionEstados.Cancelado)
        return Results.BadRequest(new { message = "Un lote completado o cancelado ya no puede modificarse." });

    var update = Builders<ProduccionDocument>.Update
        .Set(p => p.Estado, dto.Estado)
        .Set(p => p.CantidadProducida, dto.CantidadProducida)
        .Set(p => p.Notas, dto.Notas)
        .Set(p => p.UpdatedAt, DateTime.UtcNow);

    if (dto.Estado == ProduccionEstados.Completado)
        update = update.Set(p => p.CompletedAt, DateTime.UtcNow);

    var guard = Builders<ProduccionDocument>.Filter.And(
        Builders<ProduccionDocument>.Filter.Eq(p => p.Id, id),
        Builders<ProduccionDocument>.Filter.Nin(p => p.Estado, new[] { ProduccionEstados.Completado, ProduccionEstados.Cancelado }));

    var result = await produccionCollection.UpdateOneAsync(guard, update);
    if (result.ModifiedCount == 0)
        return Results.BadRequest(new { message = "El lote cambió de estado, recarga la lista." });

    if (dto.Estado == ProduccionEstados.Completado)
    {
        var productoCollection = db.GetCollection<ProductoDocument>("productos");
        var stockUpdate = Builders<ProductoDocument>.Update.Inc(p => p.Stock, dto.CantidadProducida);
        await productoCollection.UpdateOneAsync(p => p.Id == lote.ProductoId, stockUpdate);
    }

    var actualizado = await produccionCollection.Find(p => p.Id == id).FirstOrDefaultAsync();
    return Results.Ok(actualizado);
})
.WithName("UpdateProduccion")
.RequireAuthorization("AdminOnly");

app.MapDelete("/api/produccion/{id}", async (string id, IMongoDatabase db) =>
{
    if (!ObjectId.TryParse(id, out _))
        return Results.BadRequest(new { message = "ID inválido." });

    var produccionCollection = db.GetCollection<ProduccionDocument>("producciones");

    var filter = Builders<ProduccionDocument>.Filter.And(
        Builders<ProduccionDocument>.Filter.Eq(p => p.Id, id),
        Builders<ProduccionDocument>.Filter.In(p => p.Estado, new[] { ProduccionEstados.Planificado, ProduccionEstados.Cancelado }));

    var result = await produccionCollection.DeleteOneAsync(filter);
    return result.DeletedCount > 0
        ? Results.Ok(new { message = "Lote de producción eliminado." })
        : Results.BadRequest(new { message = "Solo se pueden eliminar lotes planificados o cancelados." });
})
.WithName("DeleteProduccion")
.RequireAuthorization("AdminOnly");

// ==========================================
// RESEÑAS ENDPOINTS
// ==========================================

app.MapGet("/api/resenas", async (IMongoDatabase db) =>
{
    var resenaCollection = db.GetCollection<ResenaDocument>("resenas");

    var resenas = await resenaCollection.Find(FilterDefinition<ResenaDocument>.Empty)
        .SortByDescending(r => r.CreatedAt)
        .Limit(12)
        .ToListAsync();

    var publicas = resenas.Select(r => new
    {
        r.Id,
        r.UserName,
        r.ProductoNombre,
        r.Calificacion,
        r.Comentario,
        r.CreatedAt
    });

    return Results.Ok(publicas);
})
.WithName("GetResenasPublicas");

app.MapPost("/api/resenas", async (HttpContext httpContext, [FromBody] CrearResenaDto dto, IMongoDatabase db) =>
{
    var userId = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
    if (userId is null) return Results.Unauthorized();

    if (dto.Calificacion < 1 || dto.Calificacion > 5)
        return Results.BadRequest(new { message = "La calificación debe estar entre 1 y 5." });

    if (string.IsNullOrWhiteSpace(dto.Comentario))
        return Results.BadRequest(new { message = "Escribe un comentario sobre el producto." });

    if (!ObjectId.TryParse(dto.ProductoId, out _))
        return Results.BadRequest(new { message = "ID de producto inválido." });

    var ventasCollection = db.GetCollection<VentaDocument>("ventas");
    var comproProducto = await ventasCollection.CountDocumentsAsync(
        Builders<VentaDocument>.Filter.And(
            Builders<VentaDocument>.Filter.Eq(v => v.UserId, userId),
            Builders<VentaDocument>.Filter.ElemMatch(v => v.Items, i => i.ProductoId == dto.ProductoId))) > 0;

    if (!comproProducto)
        return Results.BadRequest(new { message = "Solo puedes opinar sobre productos que hayas comprado." });

    var productoCollection = db.GetCollection<ProductoDocument>("productos");
    var producto = await productoCollection.Find(p => p.Id == dto.ProductoId).FirstOrDefaultAsync();
    if (producto is null)
        return Results.BadRequest(new { message = "El producto ya no existe." });

    var userCollection = db.GetCollection<UserDocument>("users");
    var user = await userCollection.Find(u => u.Id == userId).FirstOrDefaultAsync();
    if (user is null) return Results.Unauthorized();

    var resenaCollection = db.GetCollection<ResenaDocument>("resenas");

    var filter = Builders<ResenaDocument>.Filter.And(
        Builders<ResenaDocument>.Filter.Eq(r => r.UserId, userId),
        Builders<ResenaDocument>.Filter.Eq(r => r.ProductoId, dto.ProductoId));

    var update = Builders<ResenaDocument>.Update
        .Set(r => r.Calificacion, dto.Calificacion)
        .Set(r => r.Comentario, dto.Comentario.Trim())
        .Set(r => r.Estado, "pendiente")
        .Set(r => r.Respuesta, null)
        .Set(r => r.CreatedAt, DateTime.UtcNow)
        .SetOnInsert(r => r.UserId, userId)
        .SetOnInsert(r => r.UserName, user.Name)
        .SetOnInsert(r => r.ProductoId, dto.ProductoId)
        .SetOnInsert(r => r.ProductoNombre, producto.Nombre);

    await resenaCollection.UpdateOneAsync(filter, update, new UpdateOptions { IsUpsert = true });
    return Results.Ok(new { message = "Gracias por tu opinión." });
})
.WithName("CrearResena")
.RequireAuthorization();

app.MapGet("/api/admin/resenas", async (IMongoDatabase db) =>
{
    var resenaCollection = db.GetCollection<ResenaDocument>("resenas");

    var resenas = await resenaCollection.Find(FilterDefinition<ResenaDocument>.Empty)
        .SortByDescending(r => r.CreatedAt)
        .ToListAsync();

    return Results.Ok(resenas);
})
.WithName("AdminGetResenas")
.RequireAuthorization("AdminOnly");

app.MapPut("/api/admin/resenas/{id}", async (string id, [FromBody] SeguimientoResenaDto dto, IMongoDatabase db) =>
{
    if (!ObjectId.TryParse(id, out _))
        return Results.BadRequest(new { message = "ID inválido." });

    if (dto.Estado != "pendiente" && dto.Estado != "atendido")
        return Results.BadRequest(new { message = "Estado inválido." });

    var resenaCollection = db.GetCollection<ResenaDocument>("resenas");

    var update = Builders<ResenaDocument>.Update
        .Set(r => r.Estado, dto.Estado)
        .Set(r => r.Respuesta, string.IsNullOrWhiteSpace(dto.Respuesta) ? null : dto.Respuesta.Trim());

    var result = await resenaCollection.UpdateOneAsync(r => r.Id == id, update);
    return result.MatchedCount > 0
        ? Results.Ok(new { message = "Seguimiento actualizado." })
        : Results.NotFound(new { message = "Reseña no encontrada." });
})
.WithName("AdminSeguimientoResena")
.RequireAuthorization("AdminOnly");

// ==========================================
// COTIZACIONES ENDPOINTS
// ==========================================

app.MapPost("/api/cotizaciones", async ([FromBody] CrearCotizacionDto dto, IMongoDatabase db) =>
{
    if (string.IsNullOrWhiteSpace(dto.Nombre) || string.IsNullOrWhiteSpace(dto.Email))
        return Results.BadRequest(new { message = "Nombre y correo son obligatorios." });

    if (dto.Items is null || dto.Items.Count == 0)
        return Results.BadRequest(new { message = "Selecciona al menos un producto a cotizar." });

    if (dto.Items.Any(i => i.Cantidad <= 0))
        return Results.BadRequest(new { message = "Las cantidades deben ser mayores a cero." });

    var productoCollection = db.GetCollection<ProductoDocument>("productos");
    var items = new List<CotizacionItem>();

    foreach (var item in dto.Items)
    {
        if (!ObjectId.TryParse(item.ProductoId, out _))
            return Results.BadRequest(new { message = "La cotización incluye un producto inválido." });

        var producto = await productoCollection.Find(p => p.Id == item.ProductoId).FirstOrDefaultAsync();
        if (producto is null)
            return Results.BadRequest(new { message = "La cotización incluye un producto que no existe." });

        items.Add(new CotizacionItem
        {
            ProductoId = producto.Id!,
            NombreProducto = producto.Nombre,
            Cantidad = item.Cantidad,
            PrecioUnitario = producto.PrecioFinal, // <-- Usa PrecioFinal
            Subtotal = Math.Round(producto.PrecioFinal * item.Cantidad, 2)
        });
    }

    var cotizacion = new CotizacionDocument
    {
        Nombre = dto.Nombre.Trim(),
        Email = dto.Email.Trim(),
        Telefono = dto.Telefono?.Trim() ?? string.Empty,
        TipoPropiedad = dto.TipoPropiedad?.Trim() ?? string.Empty,
        Items = items,
        Total = Math.Round(items.Sum(i => i.Subtotal), 2),
        CreatedAt = DateTime.UtcNow
    };

    var cotizacionCollection = db.GetCollection<CotizacionDocument>("cotizaciones");
    await cotizacionCollection.InsertOneAsync(cotizacion);

    return Results.Ok(cotizacion);
})
.WithName("CrearCotizacion");

app.MapGet("/api/admin/cotizaciones", async (IMongoDatabase db) =>
{
    var cotizacionCollection = db.GetCollection<CotizacionDocument>("cotizaciones");

    var cotizaciones = await cotizacionCollection.Find(FilterDefinition<CotizacionDocument>.Empty)
        .SortByDescending(c => c.CreatedAt)
        .ToListAsync();

    return Results.Ok(cotizaciones);
})
.WithName("AdminGetCotizaciones")
.RequireAuthorization("AdminOnly");

// ==========================================
// CONTACTO ENDPOINTS
// ==========================================

app.MapPost("/api/contacto", async ([FromBody] CrearContactoDto dto, IMongoDatabase db) =>
{
    if (string.IsNullOrWhiteSpace(dto.Nombre) || string.IsNullOrWhiteSpace(dto.Email) || string.IsNullOrWhiteSpace(dto.Mensaje))
        return Results.BadRequest(new { message = "Nombre, correo y mensaje son obligatorios." });

    var mensaje = new MensajeContactoDocument
    {
        Nombre = dto.Nombre.Trim(),
        Email = dto.Email.Trim(),
        Mensaje = dto.Mensaje.Trim(),
        Atendido = false,
        CreatedAt = DateTime.UtcNow
    };

    var contactoCollection = db.GetCollection<MensajeContactoDocument>("mensajes_contacto");
    await contactoCollection.InsertOneAsync(mensaje);

    return Results.Ok(new { message = "Mensaje enviado. Te contactaremos pronto." });
})
.WithName("CrearMensajeContacto");

app.MapGet("/api/admin/contacto", async (IMongoDatabase db) =>
{
    var contactoCollection = db.GetCollection<MensajeContactoDocument>("mensajes_contacto");

    var mensajes = await contactoCollection.Find(FilterDefinition<MensajeContactoDocument>.Empty)
        .SortByDescending(m => m.CreatedAt)
        .ToListAsync();

    return Results.Ok(mensajes);
})
.WithName("AdminGetMensajesContacto")
.RequireAuthorization("AdminOnly");

app.MapPut("/api/admin/contacto/{id}/atendido", async (string id, IMongoDatabase db) =>
{
    if (!ObjectId.TryParse(id, out _))
        return Results.BadRequest(new { message = "ID inválido." });

    var contactoCollection = db.GetCollection<MensajeContactoDocument>("mensajes_contacto");

    var update = Builders<MensajeContactoDocument>.Update.Set(m => m.Atendido, true);
    var result = await contactoCollection.UpdateOneAsync(m => m.Id == id, update);

    return result.MatchedCount > 0
        ? Results.Ok(new { message = "Mensaje marcado como atendido." })
        : Results.NotFound(new { message = "Mensaje no encontrado." });
})
.WithName("AdminAtenderMensaje")
.RequireAuthorization("AdminOnly");

// ==========================================
// PROVEEDORES ENDPOINTS
// ==========================================

app.MapGet("/api/admin/proveedores", async (IMongoDatabase db) =>
{
    var proveedorCollection = db.GetCollection<ProveedorDocument>("proveedores");

    var proveedores = await proveedorCollection.Find(FilterDefinition<ProveedorDocument>.Empty)
        .SortBy(p => p.Nombre)
        .ToListAsync();

    return Results.Ok(proveedores);
})
.WithName("AdminGetProveedores")
.RequireAuthorization("AdminOnly");

app.MapPost("/api/admin/proveedores", async ([FromBody] SaveProveedorDto dto, IMongoDatabase db) =>
{
    if (string.IsNullOrWhiteSpace(dto.Nombre))
        return Results.BadRequest(new { message = "El nombre del proveedor es obligatorio." });

    var proveedor = new ProveedorDocument
    {
        Nombre = dto.Nombre.Trim(),
        Contacto = dto.Contacto?.Trim() ?? string.Empty,
        Telefono = dto.Telefono?.Trim() ?? string.Empty,
        Email = dto.Email?.Trim() ?? string.Empty,
        Direccion = dto.Direccion?.Trim(),
        CreatedAt = DateTime.UtcNow
    };

    var proveedorCollection = db.GetCollection<ProveedorDocument>("proveedores");
    await proveedorCollection.InsertOneAsync(proveedor);

    return Results.Created($"/api/admin/proveedores/{proveedor.Id}", proveedor);
})
.WithName("AdminCrearProveedor")
.RequireAuthorization("AdminOnly");

app.MapPut("/api/admin/proveedores/{id}", async (string id, [FromBody] SaveProveedorDto dto, IMongoDatabase db) =>
{
    if (!ObjectId.TryParse(id, out _))
        return Results.BadRequest(new { message = "ID inválido." });

    if (string.IsNullOrWhiteSpace(dto.Nombre))
        return Results.BadRequest(new { message = "El nombre del proveedor es obligatorio." });

    var proveedorCollection = db.GetCollection<ProveedorDocument>("proveedores");

    var update = Builders<ProveedorDocument>.Update
        .Set(p => p.Nombre, dto.Nombre.Trim())
        .Set(p => p.Contacto, dto.Contacto?.Trim() ?? string.Empty)
        .Set(p => p.Telefono, dto.Telefono?.Trim() ?? string.Empty)
        .Set(p => p.Email, dto.Email?.Trim() ?? string.Empty)
        .Set(p => p.Direccion, dto.Direccion?.Trim());

    var result = await proveedorCollection.UpdateOneAsync(p => p.Id == id, update);
    return result.MatchedCount > 0
        ? Results.Ok(new { message = "Proveedor actualizado." })
        : Results.NotFound(new { message = "Proveedor no encontrado." });
})
.WithName("AdminActualizarProveedor")
.RequireAuthorization("AdminOnly");

app.MapDelete("/api/admin/proveedores/{id}", async (string id, IMongoDatabase db) =>
{
    if (!ObjectId.TryParse(id, out _))
        return Results.BadRequest(new { message = "ID inválido." });

    var proveedorCollection = db.GetCollection<ProveedorDocument>("proveedores");

    var result = await proveedorCollection.DeleteOneAsync(p => p.Id == id);
    return result.DeletedCount > 0
        ? Results.Ok(new { message = "Proveedor eliminado." })
        : Results.NotFound(new { message = "Proveedor no encontrado." });
})
.WithName("AdminEliminarProveedor")
.RequireAuthorization("AdminOnly");

// ==========================================
// MATERIA PRIMA ENDPOINTS
// ==========================================

app.MapGet("/api/admin/materias-primas", async (IMongoDatabase db) =>
{
    var materiaPrimaCollection = db.GetCollection<MateriaPrimaDocument>("materias_primas");

    var materias = await materiaPrimaCollection.Find(FilterDefinition<MateriaPrimaDocument>.Empty)
        .SortBy(m => m.Nombre)
        .ToListAsync();

    return Results.Ok(materias);
})
.WithName("AdminGetMateriasPrimas")
.RequireAuthorization("AdminOnly");

app.MapPost("/api/admin/materias-primas", async ([FromBody] SaveMateriaPrimaDto dto, IMongoDatabase db) =>
{
    if (string.IsNullOrWhiteSpace(dto.Nombre))
        return Results.BadRequest(new { message = "El nombre de la materia prima es obligatorio." });

    if (string.IsNullOrWhiteSpace(dto.Unidad))
        return Results.BadRequest(new { message = "Indica la unidad de medida (pieza, metro, kg…)." });

    var materiaPrima = new MateriaPrimaDocument
    {
        Nombre = dto.Nombre.Trim(),
        Unidad = dto.Unidad.Trim(),
        Existencia = 0,
        CostoPromedio = 0,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    var materiaPrimaCollection = db.GetCollection<MateriaPrimaDocument>("materias_primas");
    await materiaPrimaCollection.InsertOneAsync(materiaPrima);

    return Results.Created($"/api/admin/materias-primas/{materiaPrima.Id}", materiaPrima);
})
.WithName("AdminCrearMateriaPrima")
.RequireAuthorization("AdminOnly");

app.MapPut("/api/admin/materias-primas/{id}", async (string id, [FromBody] SaveMateriaPrimaDto dto, IMongoDatabase db) =>
{
    if (!ObjectId.TryParse(id, out _))
        return Results.BadRequest(new { message = "ID inválido." });

    if (string.IsNullOrWhiteSpace(dto.Nombre) || string.IsNullOrWhiteSpace(dto.Unidad))
        return Results.BadRequest(new { message = "Nombre y unidad son obligatorios." });

    var materiaPrimaCollection = db.GetCollection<MateriaPrimaDocument>("materias_primas");

    var update = Builders<MateriaPrimaDocument>.Update
        .Set(m => m.Nombre, dto.Nombre.Trim())
        .Set(m => m.Unidad, dto.Unidad.Trim())
        .Set(m => m.UpdatedAt, DateTime.UtcNow);

    var result = await materiaPrimaCollection.UpdateOneAsync(m => m.Id == id, update);
    return result.MatchedCount > 0
        ? Results.Ok(new { message = "Materia prima actualizada." })
        : Results.NotFound(new { message = "Materia prima no encontrada." });
})
.WithName("AdminActualizarMateriaPrima")
.RequireAuthorization("AdminOnly");

app.MapDelete("/api/admin/materias-primas/{id}", async (string id, IMongoDatabase db) =>
{
    if (!ObjectId.TryParse(id, out _))
        return Results.BadRequest(new { message = "ID inválido." });

    var productoCollection = db.GetCollection<ProductoDocument>("productos");
    var enUso = await productoCollection.CountDocumentsAsync(
        Builders<ProductoDocument>.Filter.ElemMatch(p => p.Receta, r => r.MateriaPrimaId == id)) > 0;

    if (enUso)
        return Results.BadRequest(new { message = "No se puede eliminar: está en la receta de un producto." });

    var materiaPrimaCollection = db.GetCollection<MateriaPrimaDocument>("materias_primas");

    var result = await materiaPrimaCollection.DeleteOneAsync(m => m.Id == id);
    return result.DeletedCount > 0
        ? Results.Ok(new { message = "Materia prima eliminada." })
        : Results.NotFound(new { message = "Materia prima no encontrada." });
})
.WithName("AdminEliminarMateriaPrima")
.RequireAuthorization("AdminOnly");

// ==========================================
// COMPRAS A PROVEEDORES ENDPOINTS
// ==========================================

app.MapGet("/api/admin/compras", async (IMongoDatabase db) =>
{
    var compraCollection = db.GetCollection<CompraProveedorDocument>("compras_proveedores");

    var compras = await compraCollection.Find(FilterDefinition<CompraProveedorDocument>.Empty)
        .SortByDescending(c => c.CreatedAt)
        .ToListAsync();

    return Results.Ok(compras);
})
.WithName("AdminGetCompras")
.RequireAuthorization("AdminOnly");

app.MapPost("/api/admin/compras", async ([FromBody] CrearCompraDto dto, IMongoDatabase db) =>
{
    if (!ObjectId.TryParse(dto.ProveedorId, out _))
        return Results.BadRequest(new { message = "Proveedor inválido." });

    if (dto.Items is null || dto.Items.Count == 0)
        return Results.BadRequest(new { message = "La compra debe incluir al menos una materia prima." });

    if (dto.Items.Any(i => i.Cantidad <= 0 || i.CostoUnitario <= 0))
        return Results.BadRequest(new { message = "Cantidades y costos deben ser mayores a cero." });

    var proveedorCollection = db.GetCollection<ProveedorDocument>("proveedores");
    var proveedor = await proveedorCollection.Find(p => p.Id == dto.ProveedorId).FirstOrDefaultAsync();
    if (proveedor is null)
        return Results.BadRequest(new { message = "El proveedor no existe." });

    var materiaPrimaCollection = db.GetCollection<MateriaPrimaDocument>("materias_primas");
    var items = new List<CompraItem>();

    foreach (var item in dto.Items)
    {
        if (!ObjectId.TryParse(item.MateriaPrimaId, out _))
            return Results.BadRequest(new { message = "La compra incluye una materia prima inválida." });

        var materiaPrima = await materiaPrimaCollection.Find(m => m.Id == item.MateriaPrimaId).FirstOrDefaultAsync();
        if (materiaPrima is null)
            return Results.BadRequest(new { message = "La compra incluye una materia prima que no existe." });

        items.Add(new CompraItem
        {
            MateriaPrimaId = materiaPrima.Id!,
            Nombre = materiaPrima.Nombre,
            Cantidad = item.Cantidad,
            CostoUnitario = item.CostoUnitario
        });
    }

    foreach (var item in items)
    {
        var materiaPrima = await materiaPrimaCollection.Find(m => m.Id == item.MateriaPrimaId).FirstOrDefaultAsync();
        if (materiaPrima is null)
            continue;

        var existenciaNueva = materiaPrima.Existencia + item.Cantidad;
        var costoNuevo = existenciaNueva <= 0
            ? 0
            : ((materiaPrima.Existencia * materiaPrima.CostoPromedio) + (item.Cantidad * item.CostoUnitario)) / existenciaNueva;

        var update = Builders<MateriaPrimaDocument>.Update
            .Set(m => m.Existencia, existenciaNueva)
            .Set(m => m.CostoPromedio, Math.Round(costoNuevo, 2))
            .Set(m => m.UpdatedAt, DateTime.UtcNow);

        await materiaPrimaCollection.UpdateOneAsync(m => m.Id == item.MateriaPrimaId, update);
    }

    var compra = new CompraProveedorDocument
    {
        ProveedorId = proveedor.Id!,
        ProveedorNombre = proveedor.Nombre,
        Items = items,
        Total = Math.Round(items.Sum(i => i.Cantidad * i.CostoUnitario), 2),
        CreatedAt = DateTime.UtcNow
    };

    var compraCollection = db.GetCollection<CompraProveedorDocument>("compras_proveedores");
    await compraCollection.InsertOneAsync(compra);

    return Results.Created($"/api/admin/compras/{compra.Id}", compra);
})
.WithName("AdminCrearCompra")
.RequireAuthorization("AdminOnly");

// ==========================================
// ADMIN USUARIOS ENDPOINTS
// ==========================================

app.MapPost("/api/admin/users", async ([FromBody] CrearUsuarioAdminDto dto, IMongoDatabase db) =>
{
    if (string.IsNullOrWhiteSpace(dto.Name) || string.IsNullOrWhiteSpace(dto.Email))
        return Results.BadRequest(new { message = "Nombre y correo son obligatorios." });

    if (dto.Role != "client" && dto.Role != "admin")
        return Results.BadRequest(new { message = "El rol debe ser client o admin." });

    var userCollection = db.GetCollection<UserDocument>("users");

    var existente = await userCollection.Find(u => u.Email == dto.Email.Trim()).FirstOrDefaultAsync();
    if (existente is not null)
        return Results.Conflict(new { message = "El correo ya está registrado." });

    var passwordTemporal = PasswordHelper.GenerarTemporal();

    var user = new UserDocument
    {
        Name = dto.Name.Trim(),
        Email = dto.Email.Trim(),
        PasswordHash = BCryptHelper.HashPassword(passwordTemporal),
        Role = dto.Role,
        CreatedAt = DateTime.UtcNow
    };

    await userCollection.InsertOneAsync(user);

    return Results.Created($"/api/users/{user.Id}", new
    {
        user.Id,
        user.Name,
        user.Email,
        user.Role,
        passwordTemporal
    });
})
.WithName("AdminCrearUsuario")
.RequireAuthorization("AdminOnly");

app.MapDelete("/api/admin/users/{id}", async (string id, HttpContext httpContext, IMongoDatabase db) =>
{
    var requesterId = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
    if (requesterId == id)
        return Results.BadRequest(new { message = "No puedes eliminar tu propia cuenta desde aquí." });

    var userCollection = db.GetCollection<UserDocument>("users");

    var result = await userCollection.DeleteOneAsync(u => u.Id == id);
    return result.DeletedCount > 0
        ? Results.Ok(new { message = "Usuario eliminado." })
        : Results.NotFound(new { message = "Usuario no encontrado." });
})
.WithName("AdminEliminarUsuario")
.RequireAuthorization("AdminOnly");

// ==========================================
// CAMBIO DE CONTRASEÑA ENDPOINT
// ==========================================

app.MapPut("/api/users/profile/password", async (HttpContext httpContext, [FromBody] CambioPasswordDto dto, IMongoDatabase db) =>
{
    var userId = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
    if (userId is null) return Results.Unauthorized();

    if (string.IsNullOrWhiteSpace(dto.NewPassword) || dto.NewPassword.Length < 6)
        return Results.BadRequest(new { message = "La nueva contraseña debe tener al menos 6 caracteres." });

    var userCollection = db.GetCollection<UserDocument>("users");
    var user = await userCollection.Find(u => u.Id == userId).FirstOrDefaultAsync();
    if (user is null) return Results.Unauthorized();

    if (user.PasswordHash == "[GOOGLE_AUTH]")
        return Results.BadRequest(new { message = "Tu cuenta usa acceso con Google y no tiene contraseña propia." });

    if (!BCryptHelper.VerifyPassword(dto.CurrentPassword, user.PasswordHash))
        return Results.BadRequest(new { message = "La contraseña actual no es correcta." });

    var update = Builders<UserDocument>.Update.Set(u => u.PasswordHash, BCryptHelper.HashPassword(dto.NewPassword));
    await userCollection.UpdateOneAsync(u => u.Id == userId, update);

    return Results.Ok(new { message = "Contraseña actualizada exitosamente." });
})
.WithName("CambiarPassword")
.RequireAuthorization();

app.Run();
// ==========================================
// DTOs (Data Transfer Objects)
// ==========================================

public record CreateVentaDto(List<CreateVentaItemDto> Items);

public record CreateVentaItemDto(
    string ProductoId,
    string NombreProducto,
    int Cantidad,
    double PrecioUnitario
);

public record SaveProductoDto(
    string Nombre,
    string? Descripcion,
    double? PorcentajeUtilidad,
    int Stock,
    string? ImagenBase64,
    List<RecetaItemDto>? Receta = null,
    List<DocumentoProductoDto>? Documentos = null
);

public record CreateProduccionDto(
    string ProductoId,
    int CantidadPlaneada,
    string? Notas
);

public record CrearResenaDto(
    string ProductoId,
    int Calificacion,
    string Comentario
);

public record SeguimientoResenaDto(
    string Estado,
    string? Respuesta
);

public record CrearCotizacionDto(
    string Nombre,
    string Email,
    string? Telefono,
    string? TipoPropiedad,
    List<CotizacionItemDto> Items
);

public record CotizacionItemDto(
    string ProductoId,
    int Cantidad
);

public record CrearContactoDto(
    string Nombre,
    string Email,
    string Mensaje
);

public record SaveProveedorDto(
    string Nombre,
    string? Contacto,
    string? Telefono,
    string? Email,
    string? Direccion
);

public record SaveMateriaPrimaDto(
    string Nombre,
    string Unidad
);

public record CrearCompraDto(
    string ProveedorId,
    List<CompraItemDto> Items
);

public record CompraItemDto(
    string MateriaPrimaId,
    double Cantidad,
    double CostoUnitario
);

public record CrearUsuarioAdminDto(
    string Name,
    string Email,
    string Role
);

public record CambioPasswordDto(
    string CurrentPassword,
    string NewPassword
);

public record RecetaItemDto(
    string MateriaPrimaId,
    double Cantidad
);

public record DocumentoProductoDto(
    string Titulo,
    string Url
);

public record UpdateProduccionDto(
    string Estado,
    int CantidadProducida,
    string? Notas
);

public record DeviceEventDto(
    string UserId,
    string DeviceId,
    string DeviceType,
    double KwhConsumed,
    bool IsRedundantTurnOn = false
);

public record AnalyticsResultDto(
    string UserId,
    string ClusterName,
    string TextGenerated,
    double KwhSaving
);

public record MdnsDeviceDto(
    string MacAddress,
    string LocalIp,
    string DeviceType,
    string UserId
);

public record CreateDeviceDto(
    string UserId,
    string MacAddress,
    string LocalIp,
    string DeviceType,
    string DisplayName,
    string Room,
    string Icon
);

public record UpdateDeviceDto(
    string DisplayName,
    string Room,
    string Icon
);

public record DeviceStateDto(bool IsOn);

public record ScanRequestDto(string UserId);

public record RegisterRequestDto(
    string Name,
    string Email,
    string Password
);

public record LoginRequestDto(
    string Email,
    string Password
);

public record ForgotPasswordDto(string Email);

public record ResetPasswordDto(
    string ResetCode,
    string NewPassword
);

public record UpdateProfileDto(
    string Name,
    string? Phone = null,
    string? Email = null
);

public record CreateRoutineDto(
    string UserId,
    string Name,
    string Description,
    string TriggerType,
    string TriggerValue,
    List<CreateRoutineActionDto> Actions
);

public record CreateRoutineActionDto(
    string DeviceId,
    string Action,
    string Value
);

public record UpdateRoutineDto(
    string Name,
    string Description,
    bool IsActive
);

public record CreateGestureDto(
    string UserId,
    string Name,
    string GestureData,
    string? LinkedDeviceId,
    string? LinkedAction
);

public record UpdateGestureDto(
    string Name,
    string? LinkedDeviceId,
    string? LinkedAction
);

public record LinkGestureDto(
    string DeviceId,
    string Action
);

public record SpotifyTokenDto(
    string UserId,
    string AccessToken,
    string RefreshToken,
    DateTime ExpiresAt
);

public record GoogleAuthDto(string IdToken);


// ==========================================
// DOCUMENT MODELS (MongoDB Collections)
// ==========================================

public class UserSessionDocument
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? Id { get; set; }
    public string UserId { get; set; } = null!;
    public string DateString { get; set; } = null!;
    public List<DeviceLog> DeviceHistory { get; set; } = new();
}

public class DeviceLog
{
    public string DeviceId { get; set; } = null!;
    public string DeviceType { get; set; } = null!;
    public double KwhConsumed { get; set; }
    public bool IsRedundantTurnOn { get; set; }
    public DateTime Timestamp { get; set; }
}

public class SuggestionDocument
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? Id { get; set; }
    public string UserId { get; set; } = null!;
    public string AssignedCluster { get; set; } = null!;
    public string RecommendationText { get; set; } = null!;
    public double ProjectedKwhSaving { get; set; }
    public bool IsViewed { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class DeviceDocument
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public ObjectId Id { get; set; }
    public string UserId { get; set; } = null!;
    public string MacAddress { get; set; } = null!;
    public string LocalIp { get; set; } = null!;
    public string DeviceType { get; set; } = null!;
    public string DisplayName { get; set; } = null!;
    public string Room { get; set; } = null!;
    public string Icon { get; set; } = null!;
    public bool IsOnline { get; set; }
    public bool IsOn { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class UserDocument
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? Id { get; set; }
    public string Name { get; set; } = null!;
    public string Email { get; set; } = null!;
    public string? Phone { get; set; }
    public string PasswordHash { get; set; } = null!;
    public string Role { get; set; } = "client";
    public string? ResetCode { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class RoutineDocument
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public ObjectId Id { get; set; }
    public string UserId { get; set; } = null!;
    public string Name { get; set; } = null!;
    public string Description { get; set; } = null!;
    public string TriggerType { get; set; } = null!;
    public string TriggerValue { get; set; } = null!;
    public List<RoutineAction> Actions { get; set; } = new();
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class RoutineAction
{
    public string DeviceId { get; set; } = null!;
    public string Action { get; set; } = null!;
    public string Value { get; set; } = null!;
}

public class ObjectIdJsonConverter : JsonConverter<ObjectId>
{
    public override ObjectId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => ObjectId.Parse(reader.GetString()!);

    public override void Write(Utf8JsonWriter writer, ObjectId value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToString());
}

public class GestureCatalogDocument
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public ObjectId Id { get; set; }
    public string Name { get; set; } = null!;
    public string Label { get; set; } = null!;
}

public class GestureDocument
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public ObjectId Id { get; set; }
    public string UserId { get; set; } = null!;
    public string Name { get; set; } = null!;
    public string GestureData { get; set; } = null!;
    public string? LinkedDeviceId { get; set; }
    public string? LinkedAction { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class ScanDocument
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public ObjectId Id { get; set; }
    public string UserId { get; set; } = null!;
    public string Status { get; set; } = null!;
    public List<MdnsDeviceDto> DevicesFound { get; set; } = new();
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}

public class RaspberryDocument
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public ObjectId Id { get; set; }
    public string? UserId { get; set; }
    public string Name { get; set; } = null!;
    public string LocalIp { get; set; } = null!;
    public string? Contact { get; set; }
    public string Status { get; set; } = "online";
    public double CpuUsage { get; set; }
    public double RamUsage { get; set; }
    public double StorageUsage { get; set; }
    public string Uptime { get; set; } = null!;
    public DateTime LastSeen { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class ProductoDocument
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? Id { get; set; }
    public string Nombre { get; set; } = null!;
    public string Descripcion { get; set; } = null!;

    public double PorcentajeUtilidad { get; set; } = 20;
    public double PrecioBruto { get; set; }
    public double PrecioFinal { get; set; }

    public int Stock { get; set; }

    public string? ImagenBase64 { get; set; }

    public List<RecetaItem> Receta { get; set; } = new();
    public List<DocumentoProducto> Documentos { get; set; } = new();
    public DateTime CreatedAt { get; set; }
}

public class RecetaItem
{
    public string MateriaPrimaId { get; set; } = null!;
    public string Nombre { get; set; } = null!;
    public string Unidad { get; set; } = null!;
    public double Cantidad { get; set; }

    public double CostoUnitario { get; set; }
    public double Subtotal { get; set; }
}

public class DocumentoProducto
{
    public string Titulo { get; set; } = null!;
    public string Url { get; set; } = null!;
}

public class ResenaDocument
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? Id { get; set; }
    public string UserId { get; set; } = null!;
    public string UserName { get; set; } = null!;
    public string ProductoId { get; set; } = null!;
    public string ProductoNombre { get; set; } = null!;
    public int Calificacion { get; set; }
    public string Comentario { get; set; } = null!;
    public string Estado { get; set; } = "pendiente";
    public string? Respuesta { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class CotizacionDocument
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? Id { get; set; }
    public string Nombre { get; set; } = null!;
    public string Email { get; set; } = null!;
    public string Telefono { get; set; } = null!;
    public string TipoPropiedad { get; set; } = null!;
    public List<CotizacionItem> Items { get; set; } = new();
    public double Total { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class CotizacionItem
{
    public string ProductoId { get; set; } = null!;
    public string NombreProducto { get; set; } = null!;
    public int Cantidad { get; set; }
    public double PrecioUnitario { get; set; }
    public double Subtotal { get; set; }
}

public class MensajeContactoDocument
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? Id { get; set; }
    public string Nombre { get; set; } = null!;
    public string Email { get; set; } = null!;
    public string Mensaje { get; set; } = null!;
    public bool Atendido { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class ProveedorDocument
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? Id { get; set; }
    public string Nombre { get; set; } = null!;
    public string Contacto { get; set; } = null!;
    public string Telefono { get; set; } = null!;
    public string Email { get; set; } = null!;
    public string? Direccion { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class MateriaPrimaDocument
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? Id { get; set; }
    public string Nombre { get; set; } = null!;
    public string Unidad { get; set; } = null!;
    public double Existencia { get; set; }
    public double CostoPromedio { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class CompraProveedorDocument
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? Id { get; set; }
    public string ProveedorId { get; set; } = null!;
    public string ProveedorNombre { get; set; } = null!;
    public List<CompraItem> Items { get; set; } = new();
    public double Total { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class CompraItem
{
    public string MateriaPrimaId { get; set; } = null!;
    public string Nombre { get; set; } = null!;
    public double Cantidad { get; set; }
    public double CostoUnitario { get; set; }
}

public static class PasswordHelper
{
    private const string Caracteres = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789";

    public static string GenerarTemporal(int longitud = 10)
    {
        var bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(longitud);
        var sb = new StringBuilder(longitud);
        foreach (var b in bytes)
            sb.Append(Caracteres[b % Caracteres.Length]);
        return sb.ToString();
    }
}

public static class RecetaBuilder
{
    public static async Task<(List<RecetaItem> Receta, double PrecioBruto, string? Error)> Construir(
        List<RecetaItemDto>? items,
        IMongoCollection<MateriaPrimaDocument> materiasPrimas)
    {
        var receta = new List<RecetaItem>();
        double precioBruto = 0;

        if (items is null || items.Count == 0)
            return (receta, 0, null);

        foreach (var item in items)
        {
            if (item.Cantidad <= 0)
                return (receta, 0, "Las cantidades de la receta deben ser mayores a cero.");

            if (!ObjectId.TryParse(item.MateriaPrimaId, out _))
                return (receta, 0, "La receta incluye una materia prima con ID inválido.");

            var materiaPrima = await materiasPrimas.Find(m => m.Id == item.MateriaPrimaId).FirstOrDefaultAsync();
            if (materiaPrima is null)
                return (receta, 0, "La receta incluye una materia prima que no existe.");

            var subtotal = materiaPrima.CostoPromedio * item.Cantidad;
            precioBruto += subtotal;

            receta.Add(new RecetaItem
            {
                MateriaPrimaId = materiaPrima.Id!,
                Nombre = materiaPrima.Nombre,
                Unidad = materiaPrima.Unidad,
                Cantidad = item.Cantidad,
                CostoUnitario = materiaPrima.CostoPromedio,
                Subtotal = Math.Round(subtotal, 2)
            });
        }

        return (receta, Math.Round(precioBruto, 2), null);
    }

    public static List<DocumentoProducto> ConstruirDocumentos(List<DocumentoProductoDto>? documentos)
    {
        return (documentos ?? new List<DocumentoProductoDto>())
            .Where(d => !string.IsNullOrWhiteSpace(d.Titulo) && !string.IsNullOrWhiteSpace(d.Url))
            .Select(d => new DocumentoProducto { Titulo = d.Titulo.Trim(), Url = d.Url.Trim() })
            .ToList();
    }
}

public class VentaDocument
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? Id { get; set; }
    public string UserId { get; set; } = null!;
    public List<VentaItem> Items { get; set; } = new();
    public double Total { get; set; }
    public DateTime FechaVenta { get; set; }
}

public class VentaItem
{
    public string ProductoId { get; set; } = null!;
    public string NombreProducto { get; set; } = null!;
    public int Cantidad { get; set; }
    public double PrecioUnitario { get; set; }
    public double Subtotal => Cantidad * PrecioUnitario;
}

public class VentaInvalidaException(string message) : Exception(message)
{
}

public class ProduccionDocument
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? Id { get; set; }
    public string ProductoId { get; set; } = null!;
    public string ProductoNombre { get; set; } = null!;
    public int CantidadPlaneada { get; set; }
    public int CantidadProducida { get; set; }
    public string Estado { get; set; } = ProduccionEstados.Planificado;
    public string? Notas { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}

public static class ProduccionEstados
{
    public const string Planificado = "planificado";
    public const string EnProduccion = "en_produccion";
    public const string ControlCalidad = "control_calidad";
    public const string Completado = "completado";
    public const string Cancelado = "cancelado";

    private static readonly string[] Todos =
    {
        Planificado, EnProduccion, ControlCalidad, Completado, Cancelado
    };

    public static bool EsValido(string estado) => Todos.Contains(estado);
}

public static class ProductoValidator
{
    public static string? Validate(SaveProductoDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.Nombre))
            return "El nombre del producto es obligatorio.";
        if (dto.Stock < 0)
            return "El stock no puede ser negativo.";
        return null;
    }
}

public static class ServiceAuth
{
    public static bool IsValid(HttpContext context, IConfiguration configuration)
    {
        var expectedKey = configuration["ServiceApiKey"];
        if (string.IsNullOrWhiteSpace(expectedKey))
            return false;

        return context.Request.Headers.TryGetValue("X-Api-Key", out var providedKey)
            && providedKey == expectedKey;
    }
}

public static class BCryptHelper
{
    public static string HashPassword(string password)
    {
        return BCrypt.Net.BCrypt.HashPassword(password, workFactor: 12);
    }

    public static bool VerifyPassword(string password, string hash)
    {
        return BCrypt.Net.BCrypt.Verify(password, hash);
    }
}

public static class JwtHelper
{
    public static string GenerateToken(string userId, string email, string role, IConfiguration configuration)
    {
        var jwtSettings = configuration.GetSection("Jwt");
        var secretKey = Encoding.UTF8.GetBytes(jwtSettings["SecretKey"]!);
        var expirationHours = int.Parse(jwtSettings["ExpirationHours"] ?? "24");

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, userId),
            new Claim(ClaimTypes.Email, email),
            new Claim(ClaimTypes.Role, role)
        };

        var token = new JwtSecurityToken(
            issuer: jwtSettings["Issuer"],
            audience: jwtSettings["Audience"],
            claims: claims,
            expires: DateTime.UtcNow.AddHours(expirationHours),
            signingCredentials: new SigningCredentials(
                new SymmetricSecurityKey(secretKey),
                SecurityAlgorithms.HmacSha256
            )
        );

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}