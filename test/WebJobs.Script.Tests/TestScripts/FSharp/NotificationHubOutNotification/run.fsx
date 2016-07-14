﻿//----------------------------------------------------------------------------------------
// This prelude allows scripts to be edited in Visual Studio or another F# editing environment 

#if !COMPILED
#I "../../../../../bin/Binaries/WebJobs.Script.Host"
#r "Microsoft.Azure.WebJobs.Host.dll"
#r "Microsoft.Azure.WebJobs.Extensions.dll"
#endif

//----------------------------------------------------------------------------------------
// This is the implementation of the function 

open System
open System.Collections.Generic
open Microsoft.Azure.NotificationHubs
open System.Runtime.InteropServices


let GetTemplateNotification(message: string) =
    let templateProperties = new Dictionary<string, string>()
    templateProperties.["message"] <- message
    new TemplateNotification(templateProperties)

let Run(input: string, [<Out>] notification: byref<Notification>) =
    notification <- GetTemplateNotification(input)
