using Microsoft.AspNetCore.Mvc;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson.Serialization.Conventions;
using MongoDB.Driver;

var builder = WebApplication.CreateBuilder(args);

var conventionPack = new ConventionPack { new IgnoreIfNullConvention(true) };
ConventionRegistry.Register("SeenGoConventions", conventionPack, t => true);

var mongoSettings = builder.Configuration.GetSection("MongoDbSettings");
builder.Services.AddSingleton<IMongoClient>(sp => new MongoClient(mongoSettings["ConnectionString"]));
builder.Services.AddScoped(sp =>
{
    var client = sp.GetRequiredService<IMongoClient>();
    return client.GetDatabase(mongoSettings["DatabaseName"]);
});

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

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

app.MapPost("/api/telemetry/event", async ([FromBody] DeviceEventDto eventDto, IMongoDatabase db) =>
{
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
    return Results.Ok(new { message = "Telemetr\u00eda registrada exitosamente en MongoDB." });
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
.WithName("GetDeviceTelemetry");

// ==========================================
// PREDICTIVE SUGGESTIONS ENDPOINTS
// ==========================================

app.MapPost("/api/suggestions/inject-cluster-result", async ([FromBody] AnalyticsResultDto resultDto, IMongoDatabase db) =>
{
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
.WithName("GetActiveSuggestions");

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
.WithName("MarkSuggestionViewed");

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
.WithName("SyncMdnsDevices");

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
.WithName("GetDevices");

app.MapGet("/api/devices/{id}", async (string id, IMongoDatabase db) =>
{
    var deviceCollection = db.GetCollection<DeviceDocument>("devices");

    if (!ObjectId.TryParse(id, out var objectId))
        return Results.BadRequest(new { message = "ID inv\u00e1lido." });

    var device = await deviceCollection.Find(d => d.Id == objectId).FirstOrDefaultAsync();
    return device is not null
        ? Results.Ok(device)
        : Results.NotFound(new { message = "Dispositivo no encontrado." });
})
.WithName("GetDeviceById");

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
.WithName("RegisterDevice");

app.MapPut("/api/devices/{id}", async (string id, [FromBody] UpdateDeviceDto dto, IMongoDatabase db) =>
{
    var deviceCollection = db.GetCollection<DeviceDocument>("devices");

    if (!ObjectId.TryParse(id, out var objectId))
        return Results.BadRequest(new { message = "ID inv\u00e1lido." });

    var update = Builders<DeviceDocument>.Update
        .Set(d => d.DisplayName, dto.DisplayName)
        .Set(d => d.Room, dto.Room)
        .Set(d => d.Icon, dto.Icon);

    var result = await deviceCollection.UpdateOneAsync(d => d.Id == objectId, update);
    return result.ModifiedCount > 0
        ? Results.Ok(new { message = "Dispositivo actualizado." })
        : Results.NotFound(new { message = "Dispositivo no encontrado." });
})
.WithName("UpdateDevice");

app.MapDelete("/api/devices/{id}", async (string id, IMongoDatabase db) =>
{
    var deviceCollection = db.GetCollection<DeviceDocument>("devices");

    if (!ObjectId.TryParse(id, out var objectId))
        return Results.BadRequest(new { message = "ID inv\u00e1lido." });

    var result = await deviceCollection.DeleteOneAsync(d => d.Id == objectId);
    return result.DeletedCount > 0
        ? Results.Ok(new { message = "Dispositivo eliminado." })
        : Results.NotFound(new { message = "Dispositivo no encontrado." });
})
.WithName("DeleteDevice");

app.MapPut("/api/devices/{id}/state", async (string id, [FromBody] DeviceStateDto dto, IMongoDatabase db) =>
{
    var deviceCollection = db.GetCollection<DeviceDocument>("devices");

    if (!ObjectId.TryParse(id, out var objectId))
        return Results.BadRequest(new { message = "ID inv\u00e1lido." });

    var update = Builders<DeviceDocument>.Update.Set(d => d.IsOn, dto.IsOn);
    var result = await deviceCollection.UpdateOneAsync(d => d.Id == objectId, update);
    return result.ModifiedCount > 0
        ? Results.Ok(new { message = $"Dispositivo {(dto.IsOn ? "encendido" : "apagado")}." })
        : Results.NotFound(new { message = "Dispositivo no encontrado." });
})
.WithName("ToggleDeviceState");

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
.WithName("StartDeviceScan");

app.MapGet("/api/devices/scan/{scanId}", async (string scanId, IMongoDatabase db) =>
{
    var scanCollection = db.GetCollection<ScanDocument>("device_scans");

    if (!ObjectId.TryParse(scanId, out var objectId))
        return Results.BadRequest(new { message = "ID inv\u00e1lido." });

    var scan = await scanCollection.Find(s => s.Id == objectId).FirstOrDefaultAsync();
    return scan is not null
        ? Results.Ok(scan)
        : Results.NotFound(new { message = "Escaneo no encontrado." });
})
.WithName("GetScanResults");

// ==========================================
// AUTH ENDPOINTS
// ==========================================

app.MapPost("/api/auth/register", async ([FromBody] RegisterRequestDto dto, IMongoDatabase db) =>
{
    var userCollection = db.GetCollection<UserDocument>("users");

    var existing = await userCollection.Find(u => u.Email == dto.Email).FirstOrDefaultAsync();
    if (existing is not null)
        return Results.Conflict(new { message = "El correo ya est\u00e1 registrado." });

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

app.MapPost("/api/auth/login", async ([FromBody] LoginRequestDto dto, IMongoDatabase db) =>
{
    var userCollection = db.GetCollection<UserDocument>("users");

    var user = await userCollection.Find(u => u.Email == dto.Email).FirstOrDefaultAsync();
    if (user is null || !BCryptHelper.VerifyPassword(dto.Password, user.PasswordHash))
        return Results.Unauthorized();

    var token = JwtHelper.GenerateToken(user.Id.ToString(), user.Email, user.Role);
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
        return Results.Ok(new { message = "Si el correo existe, recibir\u00e1s instrucciones para restablecer tu contrase\u00f1a." });

    var resetCode = new Random().Next(100000, 999999).ToString();
    var update = Builders<UserDocument>.Update.Set(u => u.ResetCode, resetCode);
    await userCollection.UpdateOneAsync(u => u.Id == user.Id, update);

    return Results.Ok(new { message = "Si el correo existe, recibir\u00e1s instrucciones para restablecer tu contrase\u00f1a." });
})
.WithName("ForgotPassword");

app.MapPost("/api/auth/reset-password", async ([FromBody] ResetPasswordDto dto, IMongoDatabase db) =>
{
    var userCollection = db.GetCollection<UserDocument>("users");

    var user = await userCollection.Find(u => u.ResetCode == dto.ResetCode).FirstOrDefaultAsync();
    if (user is null)
        return Results.BadRequest(new { message = "C\u00f3digo de restablecimiento inv\u00e1lido." });

    var update = Builders<UserDocument>.Update
        .Set(u => u.PasswordHash, BCryptHelper.HashPassword(dto.NewPassword))
        .Unset(u => u.ResetCode);

    await userCollection.UpdateOneAsync(u => u.Id == user.Id, update);
    return Results.Ok(new { message = "Contrase\u00f1a restablecida exitosamente." });
})
.WithName("ResetPassword");

// ==========================================
// USER PROFILE ENDPOINTS
// ==========================================

app.MapGet("/api/users/{id}", async (string id, IMongoDatabase db) =>
{
    var userCollection = db.GetCollection<UserDocument>("users");

    if (!ObjectId.TryParse(id, out var objectId))
        return Results.BadRequest(new { message = "ID inv\u00e1lido." });

    var user = await userCollection.Find(u => u.Id == objectId).Project(u => new
    {
        u.Id, u.Name, u.Email, u.Role, u.CreatedAt
    }).FirstOrDefaultAsync();

    return user is not null
        ? Results.Ok(user)
        : Results.NotFound(new { message = "Usuario no encontrado." });
})
.WithName("GetUserProfile");

app.MapPut("/api/users/{id}", async (string id, [FromBody] UpdateProfileDto dto, IMongoDatabase db) =>
{
    var userCollection = db.GetCollection<UserDocument>("users");

    if (!ObjectId.TryParse(id, out var objectId))
        return Results.BadRequest(new { message = "ID inv\u00e1lido." });

    var update = Builders<UserDocument>.Update.Set(u => u.Name, dto.Name);
    var result = await userCollection.UpdateOneAsync(u => u.Id == objectId, update);
    return result.ModifiedCount > 0
        ? Results.Ok(new { message = "Perfil actualizado." })
        : Results.NotFound(new { message = "Usuario no encontrado." });
})
.WithName("UpdateUserProfile");

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
.WithName("GetRoutines");

app.MapGet("/api/routines/{id}", async (string id, IMongoDatabase db) =>
{
    var routineCollection = db.GetCollection<RoutineDocument>("routines");

    if (!ObjectId.TryParse(id, out var objectId))
        return Results.BadRequest(new { message = "ID inv\u00e1lido." });

    var routine = await routineCollection.Find(r => r.Id == objectId).FirstOrDefaultAsync();
    return routine is not null
        ? Results.Ok(routine)
        : Results.NotFound(new { message = "Rutina no encontrada." });
})
.WithName("GetRoutineById");

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
.WithName("CreateRoutine");

app.MapPut("/api/routines/{id}", async (string id, [FromBody] UpdateRoutineDto dto, IMongoDatabase db) =>
{
    var routineCollection = db.GetCollection<RoutineDocument>("routines");

    if (!ObjectId.TryParse(id, out var objectId))
        return Results.BadRequest(new { message = "ID inv\u00e1lido." });

    var update = Builders<RoutineDocument>.Update
        .Set(r => r.Name, dto.Name)
        .Set(r => r.Description, dto.Description)
        .Set(r => r.IsActive, dto.IsActive);

    var result = await routineCollection.UpdateOneAsync(r => r.Id == objectId, update);
    return result.ModifiedCount > 0
        ? Results.Ok(new { message = "Rutina actualizada." })
        : Results.NotFound(new { message = "Rutina no encontrada." });
})
.WithName("UpdateRoutine");

app.MapDelete("/api/routines/{id}", async (string id, IMongoDatabase db) =>
{
    var routineCollection = db.GetCollection<RoutineDocument>("routines");

    if (!ObjectId.TryParse(id, out var objectId))
        return Results.BadRequest(new { message = "ID inv\u00e1lido." });

    var result = await routineCollection.DeleteOneAsync(r => r.Id == objectId);
    return result.DeletedCount > 0
        ? Results.Ok(new { message = "Rutina eliminada." })
        : Results.NotFound(new { message = "Rutina no encontrada." });
})
.WithName("DeleteRoutine");

app.MapPost("/api/routines/{id}/execute", async (string id, IMongoDatabase db) =>
{
    var routineCollection = db.GetCollection<RoutineDocument>("routines");

    if (!ObjectId.TryParse(id, out var objectId))
        return Results.BadRequest(new { message = "ID inv\u00e1lido." });

    var routine = await routineCollection.Find(r => r.Id == objectId).FirstOrDefaultAsync();
    if (routine is null)
        return Results.NotFound(new { message = "Rutina no encontrada." });

    return Results.Ok(new { message = $"Rutina '{routine.Name}' ejecutada.", actions = routine.Actions });
})
.WithName("ExecuteRoutine");

// ==========================================
// GESTURE ENDPOINTS
// ==========================================

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
.WithName("GetGestures");

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
.WithName("CreateGesture");

app.MapPut("/api/gestures/{id}", async (string id, [FromBody] UpdateGestureDto dto, IMongoDatabase db) =>
{
    var gestureCollection = db.GetCollection<GestureDocument>("gestures");

    if (!ObjectId.TryParse(id, out var objectId))
        return Results.BadRequest(new { message = "ID inv\u00e1lido." });

    var update = Builders<GestureDocument>.Update
        .Set(g => g.Name, dto.Name)
        .Set(g => g.LinkedDeviceId, dto.LinkedDeviceId)
        .Set(g => g.LinkedAction, dto.LinkedAction);

    var result = await gestureCollection.UpdateOneAsync(g => g.Id == objectId, update);
    return result.ModifiedCount > 0
        ? Results.Ok(new { message = "Gesto actualizado." })
        : Results.NotFound(new { message = "Gesto no encontrado." });
})
.WithName("UpdateGesture");

app.MapDelete("/api/gestures/{id}", async (string id, IMongoDatabase db) =>
{
    var gestureCollection = db.GetCollection<GestureDocument>("gestures");

    if (!ObjectId.TryParse(id, out var objectId))
        return Results.BadRequest(new { message = "ID inv\u00e1lido." });

    var result = await gestureCollection.DeleteOneAsync(g => g.Id == objectId);
    return result.DeletedCount > 0
        ? Results.Ok(new { message = "Gesto eliminado." })
        : Results.NotFound(new { message = "Gesto no encontrado." });
})
.WithName("DeleteGesture");

app.MapPut("/api/gestures/{id}/link", async (string id, [FromBody] LinkGestureDto dto, IMongoDatabase db) =>
{
    var gestureCollection = db.GetCollection<GestureDocument>("gestures");

    if (!ObjectId.TryParse(id, out var objectId))
        return Results.BadRequest(new { message = "ID inv\u00e1lido." });

    var update = Builders<GestureDocument>.Update
        .Set(g => g.LinkedDeviceId, dto.DeviceId)
        .Set(g => g.LinkedAction, dto.Action);

    var result = await gestureCollection.UpdateOneAsync(g => g.Id == objectId, update);
    return result.ModifiedCount > 0
        ? Results.Ok(new { message = "Gesto vinculado al dispositivo." })
        : Results.NotFound(new { message = "Gesto no encontrado." });
})
.WithName("LinkGesture");

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
.WithName("GetAdminDashboardMetrics");

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
.WithName("AdminGetUsers");

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
.WithName("AdminGetDevices");

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
.WithName("AdminGetRoutines");

app.MapGet("/api/admin/raspberries", async (IMongoDatabase db) =>
{
    var raspberryCollection = db.GetCollection<RaspberryDocument>("raspberries");

    var raspberries = await raspberryCollection.Find(FilterDefinition<RaspberryDocument>.Empty).ToListAsync();
    return Results.Ok(raspberries);
})
.WithName("AdminGetRaspberries");

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
.WithName("GetConsumptionSummary");

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
.WithName("GetClientDashboard");

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
.WithName("StoreSpotifyToken");

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

app.Run();

// ==========================================
// DTOs (Data Transfer Objects)
// ==========================================

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

public record UpdateProfileDto(string Name);

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
    public ObjectId Id { get; set; }
    public string Name { get; set; } = null!;
    public string Email { get; set; } = null!;
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
    public string Name { get; set; } = null!;
    public string LocalIp { get; set; } = null!;
    public string Status { get; set; } = "online";
    public double CpuUsage { get; set; }
    public double RamUsage { get; set; }
    public double StorageUsage { get; set; }
    public string Uptime { get; set; } = null!;
    public DateTime LastSeen { get; set; }
    public DateTime CreatedAt { get; set; }
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
    public static string GenerateToken(string userId, string email, string role)
    {
        var key = new System.Text.StringBuilder();
        key.Append("SeeNGO-SecretKey-2024-Minimum32Characters!");
        return $"mock_token_{userId}_{role}";
    }
}
