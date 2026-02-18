# Welcome to your CDK C# project!

This is a blank project for CDK development with C#.


The `cdk.json` file tells the CDK Toolkit how to execute your app.

It uses the [.NET CLI](https://docs.microsoft.com/dotnet/articles/core/) to compile and execute your project.

## Build & Run Instructions

### 1. Build the project
```
dotnet restore src/InfraCdk.sln
dotnet build src/InfraCdk.sln
```

### 2. Synthesize CloudFormation template
```
cdk synth
```

### 3. Deploy to AWS
```
cdk deploy
```

### 4. Other useful commands
* `cdk diff`         compare deployed stack with current state
* `cdk destroy`      delete the deployed stack from AWS
