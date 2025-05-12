#/bin/bash

sqlpackage /Action:Publish /SourceFile:"./Database/bin/Debug/Database.dacpac" /TargetConnectionString:"Server=localhost;Database=TzDb;User Id=sa;Password=Cleaner2024;MultipleActiveResultSets=true;Encrypt=False;TrustServerCertificate=True"
SqlTzLoader/bin/Debug/net8.0/SqlTzLoader -c"Server=localhost;Database=TzDb;User Id=sa;Password=Cleaner2024;MultipleActiveResultSets=true;Encrypt=False;TrustServerCertificate=True"
