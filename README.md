# Course Waitlist Engine

## Requirement

Build a course-registration system that enforces course capacity, prevents duplicate enrollments, checks prerequisites, and places additional eligible learners on a FIFO waitlist. When a seat opens, the next eligible learner is promoted.

## Tech stack

- **Backend:** ASP.NET Core Web API, .NET 8, C#
- **Frontend:** Angular 21, TypeScript
- **API documentation:** Swagger
- **Storage:** In-memory

## Prerequisites

- .NET 8 SDK
- Node.js and npm

## Run the application

Start the backend from the repository root:

```bash
cd dotnet-backend/CourseRegistration.Api
dotnet run
```

Open the Swagger API page in your browser:

http://127.0.0.1:5011/swagger/index.html

In a separate terminal, install frontend dependencies and start Angular:

```bash
cd angular-frontend
npm install
npm start
```

The frontend runs at http://localhost:4200. The `node_modules/` directory is ignored by Git; run `npm install` to recreate it.
