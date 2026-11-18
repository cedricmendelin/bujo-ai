namespace Bujo.Application;

public interface IJournalExport
{
    JournalExportDocument Create(Bujo.Domain.JournalSnapshot snapshot);
}
