# ADO Work Item Transfer

Transfer work items from one Azure DevOps instance to another. You'll need to setup your target environment to contain the same Work Item types.

Capabilities:
- Basic field transfers including work item types, description, title. Can easily be modified to add more
- Pasted images into the work Items
- Comments (as the personal access token user)
- Parent/Child relationships

Limitations:
- Attachments on the work items don't transfer


## How to run
Simply add your azure devOps environment information, then simply run the program.

    dotnet run