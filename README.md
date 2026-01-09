# 🚀 InfiniteGPU

<img width="1907" height="1090" alt="image" src="https://github.com/user-attachments/assets/0e169d5f-3a19-43ed-93e6-607a1f1d12b6" />


InfiniteGPU is a production-ready platform that enables effortless exchange of compute resources for AI workloads. Requestors can offload intensive AI inference tasks to a distributed network of providers, while providers monetize their idle NPU/GPU/CPU capacity, orchestrated through a web interface and native desktop application.

## 🎯 Project Goal

Transform how compute power is accessed and shared by creating a frictionless marketplace where:
- **Requestors** can execute AI inference tasks without expensive infrastructure
- **Providers** earn passive income by sharing their device's computing resources
- **The platform** orchestrates task distribution, execution monitoring, and automated payments

## ✨ Key Features

- 🧠 **ONNX Model Execution** - Run AI inference & training tasks using industry-standard ONNX models
- 𓇲 **Neural processing units** - Ability to target NPUs can accelerate AI inference & training
- 📁 **Multiple input/outputs formats** - Input can be plain text, images, videos, numpy tensors
- 💰 **Automated Payments** - Stripe integration with platform commission, centralized
- ⚡ **Real-time Updates** - SignalR-powered live task status and progress tracking
- 🖥️ **Native Desktop Client** - WinUI 3 application for seamless and native background compute execution
- 🔐 **Secure Authentication** - JWT-based auth with comprehensive user management
- 📊 **Financial Dashboard** - Track earnings, settlements, and payment history
- 🎨 **Modern UI** - Beautiful, responsive interface built with React and TailwindCSS

## 🏗️ Architecture

### Backend
**ASP.NET Core 10.0** minimal API with clean architecture patterns

- **CQRS Pattern** via MediatR for command/query separation
- **Entity Framework Core** with SQL Server for data persistence
- **ASP.NET Identity** for user management and authentication
- **SignalR Hubs** for real-time bidirectional communication
- **FluentValidation** for robust input validation
- **Azure Blob Storage** for task data and model file storage
- **Stripe API** for payment processing and webhook handling

### Frontend
**React 19** with modern tooling and state management

- **Vite** for lightning-fast development and optimized builds
- **TailwindCSS v4** for utility-first styling
- **Radix UI** for accessible, unstyled component primitives
- **TanStack Query** for powerful async state management
- **Zustand** for lightweight client state
- **React Hook Form + Zod** for type-safe form handling
- **Framer Motion** for smooth animations

### Desktop Application
**WinUI 3** native Windows application

- **ONNX Runtime** for high-performance AI inference execution (on CPU, GPU and NPU)
- **OpenCV Sharp** for image processing and computer vision tasks
- **SignalR Client** for task orchestration and status updates
- **System.Management** for hardware metrics collection
- **Background Services** for autonomous task execution

## 📂 Project Structure

```
Scalerize.InfiniteGpu/
├── backend/
│   └── InfiniteGPU.Backend/
│       ├── Features/           # Feature-based modules (CQRS)
│       │   ├── Auth/          # Authentication & user management
│       │   ├── Tasks/         # Task creation and orchestration
│       │   ├── Subtasks/      # Provider task claiming & execution
│       │   ├── Finance/       # Payments, earnings, settlements
│       │   └── Inference/     # AI inference endpoints
│       ├── Shared/            # Cross-cutting concerns
│       │   ├── Services/      # JWT, Email, Task assignment
│       │   ├── Hubs/          # SignalR real-time hubs
│       │   └── Models/        # Shared DTOs and enums
│       ├── Data/              # EF Core DbContext & entities
│       └── Migrations/        # Database migrations
│
├── frontend/
│   └── src/
│       ├── features/          # Feature modules
│       │   ├── auth/          # Login, register, profile
│       │   ├── requestor/     # Task requests and monitoring
│       │   └── provider/      # Earnings and task execution
│       ├── pages/             # Route-level components
│       ├── shared/            # Shared utilities
│       │   ├── components/    # Reusable UI components
│       │   ├── layout/        # App shell and navigation
│       │   ├── stores/        # Zustand stores
│       │   └── utils/         # API client, helpers
│       └── assets/            # Static assets
│
├── desktop/
│   └── Scalerize.InfiniteGpu.Desktop/
│       └── Scalerize.InfiniteGpu.Desktop/
│           ├── Services/      # Background work, ONNX execution
│           ├── Assets/        # App icons and resources
│           └── MainWindow.xaml # Main application window
│
└── docs/                      # Architecture documentation
```

## 🚀 Quick Start

### Prerequisites
- **.NET 8.0 SDK** or later
- **Node.js 18+** and npm
- **SQL Server** (LocalDB or full instance)
- **Visual Studio 2022** (for desktop app development)

### 1. Backend Setup

```bash
cd backend/InfiniteGPU.Backend

# Restore dependencies
dotnet restore

# Update database (creates schema)
dotnet ef database update

# Run the backend (starts on http://localhost:5000)
dotnet watch run
```

**API Documentation:** Navigate to `http://localhost:5000/swagger` when running

### 2. Frontend Setup

```bash
cd frontend

# Install dependencies
npm install

# Start development server (http://localhost:5173)
npm run dev
```

### 3. Desktop Application Setup

```bash
cd desktop/Scalerize.InfiniteGpu.Desktop

# Open in Visual Studio 2022
start Scalerize.InfiniteGpu.Desktop.slnx

# Build and run the desktop client
# Set startup project to Scalerize.InfiniteGpu.Desktop (Package)
# Press F5 to run
```

**If dependencies are already installed**: `./dev.ps1` launch dotnet and npm.

### 4. Environment Configuration

Copy `.env.example` to `.env` in the root directory and frontend and configure:

```env
# Database
ConnectionStrings__DefaultConnection="Server=..."

# JWT Configuration
Jwt__Key="your-secret-key-here"
Jwt__Issuer="InfiniteGPU"
Jwt__Audience="InfiniteGPU"

# Stripe (for payments)
Stripe__SecretKey="sk_test_..."
Stripe__WebhookSecret="whsec_..."

# Email (Mailgun)
Mailgun__ApiKey="your-mailgun-api-key"
Mailgun__Domain="your-domain.com"

# Azure Storage (for task files)
AzureStorage__ConnectionString="DefaultEndpointsProtocol=https..."

# Frontend URL (for CORS)
Frontend__Url="http://localhost:5173"
```

## 🔧 Technology Stack

### Backend Technologies
- **Runtime:** .NET 10.0
- **Framework:** ASP.NET Core Minimal APIs
- **Database:** SQL Server with Entity Framework Core 
- **Authentication:** ASP.NET Identity + JWT Bearer
- **Architecture:** CQRS via MediatR 
- **Validation:** FluentValidation 
- **Real-time:** SignalR
- **Payments:** Stripe.NET 
- **Storage:** Azure Blob Storage 
- **Documentation:** Swagger/OpenAPI

### Frontend Technologies
- **Framework:** React 19.1
- **Build Tool:** Vite
- **Language:** TypeScript
- **Styling:** TailwindCSS 4.1
- **UI Components:** Radix UI
- **State Management:** Zustand + TanStack Query 
- **Forms:** React Hook Form + Zod validation
- **Routing:** React Router
- **Icons:** Lucide React
- **Animations:** Framer Motion
- **Real-time:** @microsoft/signalr

### Desktop Technologies
- **Framework:** WinUI 3
- **Runtime:** .NET 10.0
- **AI Inference:** Microsoft.ML.OnnxRuntime 
- **Image Processing:** OpenCvSharp4, ImageSharp 
- **Real-time:** SignalR Client 
- **DI Container:** Microsoft.Extensions.DependencyInjection 
- **System Metrics:** System.Management
- **Tray Icon:** H.NotifyIcon.WinUI

## 📱 Application Flow

1. **Requestor Journey**
   - Register/Login to the platform
   - Upload ONNX model and create inference task
   - Configure task parameters and parallelization
   - Fund wallet via Stripe payment
   - Monitor task progress in real-time via SignalR
   - Download results when complete

2. **Provider Journey**
   - Install desktop application
   - Register device and authenticate
   - Desktop app runs in background
   - Automatically claims and executes available subtasks
   - Earns credits for successful completions
   - Request withdrawals when threshold reached

3. **Platform Operations**
   - Orchestrates task distribution to available providers
   - Monitors subtask execution via heartbeats
   - Handles failures with automatic reassignment
   - Processes payments and calculates earnings
   - Tracks 10% commission on transactions

## 🧪 Development Commands

### Backend
```bash
# Run with hot reload
dotnet watch run

# Run tests
dotnet test

# Create migration
dotnet ef migrations add MigrationName

# Apply migrations
dotnet ef database update

# Generate SQL script
dotnet ef migrations script
```

### Frontend
```bash
# Development server
npm run dev

# Production build
npm run build

# Preview production build
npm run preview

# Lint code
npm run lint
```

### Desktop
```bash
# Build for specific platform
dotnet publish -c Release -r win-x64

# Create package
msbuild /t:Publish /p:Configuration=Release
```

## 🤝 Contributing

Contributions are welcome! Please feel free to submit pull requests or open issues for bugs and feature requests.

