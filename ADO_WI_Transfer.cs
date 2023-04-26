using Microsoft.TeamFoundation.WorkItemTracking.WebApi;
using Microsoft.TeamFoundation.WorkItemTracking.WebApi.Models;
using Microsoft.VisualStudio.Services.Common;
using Microsoft.VisualStudio.Services.WebApi.Patch.Json;
using Microsoft.VisualStudio.Services.WebApi.Patch;


string ado_org = "";
string ado_project = "";
string ado_project_id = "";
string ado_username = "";
string ado_pat = "";
Uri ado_uri = new Uri($"https://dev.azure.com/{ado_org}");

string to_ado_org = "";
string to_ado_project = "";
string to_ado_project_id = "";
string to_ado_username = "";
string to_ado_pat = "";
Uri to_ado_uri = new Uri($"https://dev.azure.com/{to_ado_org}");

string acceptanceCriteriaFieldName = "Microsoft.VSTS.Common.AcceptanceCriteria";

VssBasicCredential credentials = new VssBasicCredential(ado_username, ado_pat);
VssBasicCredential destinationCredentials = new VssBasicCredential(to_ado_username, to_ado_pat);

Console.WriteLine("Starting work item transfer");

var sourceHttpClient = new WorkItemTrackingHttpClient(ado_uri, credentials);
var destinationHttpClient = new WorkItemTrackingHttpClient(to_ado_uri, destinationCredentials);

var wiql = new Wiql()
{
    Query = "Select [Id] " +
            "From WorkItems " +
            $"Where [System.TeamProject] = '{ado_project}' " + 
            "AND [System.WorkItemType] == 'Feedback Request'"
};
var result = sourceHttpClient.QueryByWiqlAsync(wiql).Result;
var ids = result.WorkItems.Select(item => item.Id).ToArray();

//Create work items
foreach (var sourceWorkItemId in ids)
{
    var workItem = sourceHttpClient.GetWorkItemAsync(sourceWorkItemId, expand: WorkItemExpand.All).Result;

    Console.WriteLine($"Transfering {sourceWorkItemId}: {workItem.Fields["System.Title"]}");

    string[] fieldsToCopy = new String[]{
            "System.WorkItemType",
            // "System.State", //ADO requires new work items to be created as New. Can add state change if necessary
            "System.Title",
            "System.Description",
            "System.Tags"
        };

    JsonPatchDocument document = new JsonPatchDocument();

    // Add in acceptance criteria
    if (workItem.Fields.Keys.Contains(acceptanceCriteriaFieldName))
    {
        string acceptanceCriteriaFieldValue = (string)workItem.Fields[acceptanceCriteriaFieldName];

        //Upload pasted images into the destination project and swap with new attachment ids
        extractAttachmentIds(acceptanceCriteriaFieldValue).ForEach(attachmentId =>
        {
            Console.WriteLine($"{workItem.Id} has pasted attachment {attachmentId}. Copying to new project");
            var attachmentRef = destinationHttpClient.CreateAttachmentAsync(sourceHttpClient.GetAttachmentContentAsync(ado_project, new Guid(attachmentId)).Result, new Guid(to_ado_project_id), "image.png").Result;
            Console.WriteLine($"Created attachment {attachmentRef.Id}");
            acceptanceCriteriaFieldValue = acceptanceCriteriaFieldValue.Replace(attachmentId, attachmentRef.Id.ToString()); //Swap Id's
        });

        document.Add(
            buildJsonPatchOperation(
            acceptanceCriteriaFieldName,
            acceptanceCriteriaFieldValue.Replace($"/{ado_org}/{ado_project_id}/_apis/wit/attachments/", $"/{to_ado_org}/{to_ado_project_id}/_apis/wit/attachments/") //Replace previous project references with destination references
        )
        );
    }

    // Copy all specified fields
    foreach (string fieldPath in fieldsToCopy)
    {
        if (workItem.Fields.Keys.Contains(fieldPath))
        {
            document.Add(buildJsonPatchOperation(fieldPath, workItem.Fields[fieldPath]));
        }
    }

    // Create work item
    WorkItem createdWorkItem = destinationHttpClient.CreateWorkItemAsync(document, to_ado_project, workItem.Fields["System.WorkItemType"].ToString()).Result;
    
    // Transfer comments
    sourceHttpClient.GetCommentsAsync(ado_project, sourceWorkItemId).Result.Comments.ForEach(comment =>
    {
        destinationHttpClient.AddCommentAsync(
            new CommentCreate()
            {
                Text = $"{comment.CreatedDate} {comment.CreatedBy.DisplayName}: {comment.Text}"
            },
            to_ado_project,
            (int)createdWorkItem.Id
        );
    });
}

Console.WriteLine($"Creating Parent/Child relationships");
foreach (var workItemId in ids)
{
    var workItem = sourceHttpClient.GetWorkItemAsync(workItemId, expand: WorkItemExpand.All).Result;

    Console.WriteLine($"Adding relationships for {workItemId}");

    JsonPatchDocument document = new JsonPatchDocument();
    // Flag attachments (only 2 to transfer so lets just manually do this)
    if (workItem.Relations != null)
    {
        workItem.Relations.ForEach(relation =>
            {
                switch (relation.Rel)
                {
                    case "AttachedFile":
                        Console.WriteLine($"Manually transfer attachments for {workItem.Id}");
                        break;
                    case "System.LinkTypes.Hierarchy-Reverse":

                        string sourceParentId = relation.Url.Substring(relation.Url.LastIndexOf("/") + 1); //Extract parent id from url
                        string sourceParentTitle = (string)sourceHttpClient.GetWorkItemAsync(ado_project, Int32.Parse(sourceParentId)).Result.Fields["System.Title"]; // Use id to look up the title from source
                        string destinationParentWorkItemId = getWorkItemIdFromTitle(sourceParentTitle, destinationHttpClient).ToString(); // Use title to query for destination work Item parent ID

                        document.Add(new JsonPatchOperation()
                        {
                            Operation = Operation.Add,
                            Path = "/relations/-",
                            Value = new WorkItemRelation()
                            {
                                Rel = "System.LinkTypes.Hierarchy-Reverse",
                                Url = relation.Url.Replace(ado_org, to_ado_org).Replace(sourceParentId, destinationParentWorkItemId)
                            }
                        });
                        break;
                }
            }
        );
        if (document.Count > 0)
        {

            await destinationHttpClient.UpdateWorkItemAsync(document, getWorkItemIdFromTitle((string)workItem.Fields["System.Title"], destinationHttpClient));
        }
        else
        {
            Console.WriteLine("No relations to add");
        }
    }
}


//Helper to build json patch doc
JsonPatchOperation buildJsonPatchOperation(string field, object? value)
{
    return new JsonPatchOperation()
    {
        Operation = Operation.Add,
        Path = $"/fields/{field}",
        Value = value
    };
}

// Finds all of the attachment id's in a text field so they can be downloaded
List<string> extractAttachmentIds(string fieldValue)
{
    List<String> ids = new List<string>();

    string searchString = $"https://dev.azure.com/{ado_org}/{ado_project_id}/_apis/wit/attachments/";
    int startSearch = 0;
    int currentIndex;

    while ((currentIndex = fieldValue.IndexOf(searchString, startSearch)) >= 0)
    {
        ids.Add(fieldValue.Substring(currentIndex + searchString.Length, 36));
        startSearch = currentIndex + 36;
    }

    return ids;
}

int getWorkItemIdFromTitle(string title, WorkItemTrackingHttpClient client)
{
    //Query for already created work item based on Title
    var workItemQuery = new Wiql()
    {
        Query = "Select [Id] " +
            "From WorkItems " +
            $"Where [System.Title] = '{title}'"
    };

    return client.QueryByWiqlAsync(workItemQuery).Result.WorkItems.First().Id;
}