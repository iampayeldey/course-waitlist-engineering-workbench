using CourseRegistration.Domain.Models;
using CourseRegistration.Domain.Services;

// Application startup entry point for the Course Registration API.
// This file configures dependency injection, web server settings, CORS,
// Swagger documentation, and the HTTP pipeline for the application.

var builder = WebApplication.CreateBuilder(args);

// Bind the ASP.NET Core app to a local fixed port for development.
// This makes it easier to call the API from Angular or Postman during local testing.
// If the port is already in use, the app will fail to start with a bind exception.
builder.WebHost.UseUrls("http://127.0.0.1:5011");

// Register the in-memory course offering service as a singleton.
// A singleton is used because the application maintains one shared course state
// during the lifetime of the service. This is suitable for demo/testing scenarios.
builder.Services.AddSingleton<ICourseOfferingService>(sp =>
{
    // Initial course configuration used by the service when application starts.
    // This represents a sample offering for a course like "Data Structures & Algorithms".
    var config = new CourseOfferingConfig(
        "CS201-FALL26",
        "CS-201",
        "Data Structures & Algorithms",
        3,
        new List<string> { "CS-101" }
    );

    return new CourseOfferingService(config);
});

// Add MVC controllers so API endpoints can be resolved from controller actions.
builder.Services.AddControllers();

// Enable OpenAPI/Swagger metadata generation for API documentation.
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Configure Cross-Origin Resource Sharing (CORS) for frontend applications,
// such as Angular running on localhost:4200 or localhost:3000.
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAngularApp", policy =>
    {
        policy.WithOrigins("http://localhost:4200", "http://localhost:3000")
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});

var app = builder.Build();

// Expose Swagger UI in the development environment for easy API testing and review.
// This helps new developers understand available endpoints without reading code alone.
app.UseSwagger();
app.UseSwaggerUI();

// Enable CORS with the policy named "AllowAngularApp".
app.UseCors("AllowAngularApp");

// Authentication/authorization middleware can be added here when needed.
app.UseAuthorization();

// Map controller-based endpoints such as /api/CourseOffering/enroll.
app.MapControllers();

// A simple root endpoint to confirm the API is up and reachable.
// Useful during debugging and smoke testing.
app.MapGet("/", () => "API is running for Payel");

// Starts the web server and begins listening for incoming HTTP requests.
app.Run();

// Notes for future developers:
// 1. If port 5011 is already in use, the application will fail to start.
// 2. Swagger is enabled here for easier API review and testing.
// 3. The application stores course registration state in memory for this demo.
// 4. For production, consider replacing the in-memory singleton with a database-backed service.

























