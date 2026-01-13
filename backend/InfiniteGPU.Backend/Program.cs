using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Azure.Storage.Blobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using DotNetEnv;
using FluentValidation;
using InfiniteGPU.Backend.Data;
using InfiniteGPU.Backend.Data.Entities;
using InfiniteGPU.Backend.Shared.Options;
using InfiniteGPU.Backend.Shared.Services;
using InfiniteGPU.Backend.Features.Auth.Endpoints;
using InfiniteGPU.Backend.Features.Tasks.Endpoints;
using InfiniteGPU.Backend.Features.Inference.Endpoints;
using InfiniteGPU.Backend.Features.Finance.Endpoints;
using InfiniteGPU.Backend.Features.Subtasks.Endpoints;
using InfiniteGPU.Backend.Shared.Hubs;

Env.TraversePath().Load();

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.Configure<FrontendOptions>(builder.Configuration.GetSection("Frontend"));

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        var frontendOptions = builder.Configuration
            .GetRequiredSection("Frontend")
            .Get<FrontendOptions>();

        policy.WithOrigins(frontendOptions!.AllowedOrigins)
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials();
    });
});

builder.Services.Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(opt =>
{
    opt.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

// Database
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(
        builder.Configuration.GetConnectionString("DefaultConnection"),
        sqlServerOptions => sqlServerOptions.EnableRetryOnFailure(
            maxRetryCount: 5,
            maxRetryDelay: TimeSpan.FromSeconds(30),
            errorNumbersToAdd: null)));

// Identity
builder.Services.AddIdentity<ApplicationUser, IdentityRole>(options =>
{
    options.Password.RequireDigit = true;
    options.Password.RequiredLength = 8;
    options.Password.RequireNonAlphanumeric = true;
    options.Password.RequireUppercase = true;
    options.Password.RequireLowercase = true;
    options.User.RequireUniqueEmail = true;
})
.AddEntityFrameworkStores<AppDbContext>()
.AddDefaultTokenProviders();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "InfiniteGPU API",
        Version = "v1",
        Description = "API surface for InfiniteGPU backend services."
    });

    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Description = "JWT Authorization header using the Bearer scheme. Example: \"Authorization: Bearer {token}\"",
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT"
    });

    options.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "Bearer"
                }
            },
            Array.Empty<string>()
        }
    });
});

// JWT Authentication
builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
})
.AddJwtBearer(options =>
{
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        ValidIssuer = builder.Configuration["Jwt:Issuer"],
        ValidAudience = builder.Configuration["Jwt:Audience"],
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(builder.Configuration["Jwt:Key"]!))
    };

    options.Events = new JwtBearerEvents
    {
        OnMessageReceived = context =>
        {
            var accessToken = context.Request.Query["access_token"];
            var path = context.HttpContext.Request.Path;

            if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/taskhub"))
            {
                context.Token = accessToken;
            }

            return System.Threading.Tasks.Task.CompletedTask;
        }
    };
});

builder.Services.AddAuthorization();

// MediatR
builder.Services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(typeof(Program).Assembly));

// FluentValidation
builder.Services.AddValidatorsFromAssemblyContaining<Program>();

// SignalR
builder.Services.AddSignalR();

builder.Services.AddOptions<AzureStorageOptions>()
    .Bind(builder.Configuration.GetSection("AzureStorage"))
    .ValidateDataAnnotations()
    .Validate(options => !string.IsNullOrWhiteSpace(options.ConnectionString), "Azure storage connection string must be provided.");

builder.Services.Configure<MailgunOptions>(builder.Configuration.GetSection("Mailgun"));

// Application services
builder.Services.AddSingleton(sp =>
{
    var storageOptions = sp.GetRequiredService<IOptions<AzureStorageOptions>>().Value;
    return new BlobServiceClient(storageOptions.ConnectionString);
});

builder.Services.AddScoped<ITaskUploadUrlService, TaskUploadUrlService>();
builder.Services.AddScoped<JwtService>();
builder.Services.AddScoped<TaskAssignmentService>();
builder.Services.AddScoped<ApiKeyAuthenticationService>();
builder.Services.AddHttpClient<MailgunEmailSender>();
builder.Services.AddTransient<IEmailSender, MailgunEmailSender>();

// Smart Partitioning Services
// Removed SubtaskPartitionManager registration
builder.Services.AddScoped<DistributedTaskOrchestrator>();


var app = builder.Build();

// Apply pending migrations automatically on startup
using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    dbContext.Database.Migrate();
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/swagger/v1/swagger.json", "InfiniteGPU API v1");
        options.RoutePrefix = "swagger";
    });
}

// Configure the HTTP request pipeline.

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.UseStaticFiles();

app.MapAuthEndpoints();
app.MapTaskEndpoints();
app.MapProviderSubtaskEndpoints();
app.MapFinanceEndpoints();
app.MapStripeWebhookEndpoints();
app.MapInferenceEndpoints();

app.MapHub<TaskHub>("/taskhub");

app.Run();
