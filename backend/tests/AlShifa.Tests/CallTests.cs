using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AlShifa.Api.Features.Auth;
using AlShifa.Api.Features.Calls;
using AlShifa.Api.Features.Clinical;
using AlShifa.Api.Features.Finance;
using AlShifa.Api.Features.Patients;
using AlShifa.Api.Features.Users;
using AlShifa.Api.Features.Visits;
using AlShifa.Api.Infrastructure;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AlShifa.Tests;

[Collection("api")]
public sealed class CallTests(ApiFixture api) : PhysicalQueueTestScope(api)
{
    [Fact]
    public async Task Late_card_three_waits_for_current_card_four_then_precedes_five_without_replacing_the_visit()
    {
        var (doctor, doctorId) = await DoctorInRoomAsync(7);
        var (station, stationId) = await StationAsync();
        var visits = new List<VisitRow>();
        for (var i = 1; i <= 5; i++) visits.Add((await NewVisitAsync(doctorId)).Visit);
        var three = visits[2]; var four = visits[3]; var five = visits[4];
        Assert.Equal((3, 4, 5), (three.QueueNumber, four.QueueNumber, five.QueueNumber));
        foreach (var visit in visits.Skip(2)) await api.CompleteVisionAsync(visit.Id);
        Assert.True((await doctor.PostAsJsonAsync("/api/calls/presence", new QueuePresenceRequest(CallDesk.Doctor, three.Id, null, false))).IsSuccessStatusCode);
        var first = await Read<CallRow>(await Call(doctor, CallDesk.Doctor, four.Id), HttpStatusCode.Created);
        Assert.Equal(4, first.QueueNumber);
        var audio = (await NextAsync(station, stationId))!;
        Assert.Equal(VoicePack.RecordedClips(4, VoicePack.Room(7), CallDesk.Doctor), audio.Clips);
        await Finish(station, stationId, audio.Id);
        // Card four's exam is saved: four is seen and leaves the queue, but is still in the room and so is never marked absent.
        Assert.True((await doctor.PutAsJsonAsync($"/api/consultations/{four.Id}",
            new SaveConsultationRequest(null, null, "پلان", null, [new DiagnosisInput(EyeSide.Both, "Dry eye")], null))).IsSuccessStatusCode);
        var current = await Read<VisitRow>(await doctor.GetAsync($"/api/visits/{four.Id}"));
        Assert.Equal(nameof(VisitStatus.InConsultation), current.Status);
        Assert.True((await doctor.PostAsJsonAsync("/api/calls/presence", new QueuePresenceRequest(CallDesk.Doctor, three.Id, null, true))).IsSuccessStatusCode);
        await AssertRefused(await doctor.PostAsJsonAsync("/api/calls/presence", new QueuePresenceRequest(CallDesk.Doctor, four.Id, null, false)), "patient_in_service");
        var queue = await Read<CallDeskView>(await doctor.GetAsync("/api/calls/desk/Doctor"));
        Assert.DoesNotContain(queue.Rows, r => r.VisitId == four.Id);
        Assert.Equal(3, queue.NextNumber);
        var tv = await Read<JsonElement>(await station.GetAsync("/api/calls/display/doctor-queue"));
        Assert.Equal(3, tv.GetProperty("nextNumber").GetInt32());
        current = await Read<VisitRow>(await doctor.GetAsync($"/api/visits/{four.Id}"));
        await Read<VisitRow>(await doctor.PostAsJsonAsync($"/api/visits/{four.Id}/status", new ChangeVisitStatusRequest(VisitStatus.Completed, current.Version)));
        await AssertRefused(await Call(doctor, CallDesk.Doctor, five.Id), "queue_priority");
        var next = await Read<CallRow>(await Call(doctor, CallDesk.Doctor, three.Id), HttpStatusCode.Created);
        Assert.Equal(3, next.QueueNumber);
        Assert.Equal(three.Id, await InDbAsync(db => db.Visits.Where(v => v.Id == three.Id && v.QueueNumber == 3).Select(v => v.Id).SingleAsync()));
        await Finish(station, stationId, (await NextAsync(station, stationId))!.Id);
        // Card three is called but not yet in consultation: the TV shows it with the doctor and five as next, never three twice.
        tv = await Read<JsonElement>(await station.GetAsync("/api/calls/display/doctor-queue"));
        Assert.Equal(3, tv.GetProperty("calledNumber").GetInt32());
        Assert.Equal(5, tv.GetProperty("nextNumber").GetInt32());
    }

    [Fact]
    public async Task Opening_the_chart_starts_only_the_called_patient_and_an_unrecorded_start_can_still_be_marked_absent()
    {
        var (doctor, doctorId) = await DoctorInRoomAsync(9);
        var one = (await NewVisitAsync(doctorId)).Visit;
        var two = (await NewVisitAsync(doctorId)).Visit;
        await api.CompleteVisionAsync(one.Id); await api.CompleteVisionAsync(two.Id);
        Task<VisitStatus> StatusOf(long id) => InDbAsync(db => db.Visits.Where(v => v.Id == id).Select(v => v.Status).SingleAsync());
        Task<HttpResponseMessage> Open(long id) => doctor.PostAsync($"/api/consultations/{id}/start", null);

        // Reading a chart before calling changes nothing.
        Assert.Equal(HttpStatusCode.NoContent, (await Open(one.Id)).StatusCode);
        Assert.Equal(VisitStatus.ReadyForDoctor, await StatusOf(one.Id));

        await Read<CallRow>(await Call(doctor, CallDesk.Doctor, one.Id), HttpStatusCode.Created);
        Assert.Equal(HttpStatusCode.NoContent, (await Open(one.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Open(one.Id)).StatusCode);
        Assert.Equal(VisitStatus.InConsultation, await StatusOf(one.Id));
        Assert.Equal(1, await InDbAsync(db => db.VisitStatusHistory.CountAsync(h => h.VisitId == one.Id && h.ToStatus == VisitStatus.InConsultation)));
        Assert.Equal(1, await InDbAsync(db => db.PatientCalls.CountAsync(c => c.VisitId == one.Id)));

        // Another patient's chart is only read.
        Assert.Equal(HttpStatusCode.NoContent, (await Open(two.Id)).StatusCode);
        Assert.Equal(VisitStatus.ReadyForDoctor, await StatusOf(two.Id));

        // Opened, but the patient never came and nothing was written: absent puts the visit back to ready.
        Assert.True((await doctor.PostAsJsonAsync("/api/calls/presence", new QueuePresenceRequest(CallDesk.Doctor, one.Id, null, false))).IsSuccessStatusCode);
        Assert.Equal(VisitStatus.ReadyForDoctor, await StatusOf(one.Id));
        var queue = await Read<CallDeskView>(await doctor.GetAsync("/api/calls/desk/Doctor"));
        Assert.Equal(two.QueueNumber, queue.NextNumber);
    }

    [Fact]
    public async Task A_patient_with_no_assigned_doctor_stays_in_the_queue_of_the_doctor_who_opened_the_chart()
    {
        var (doctor, _) = await DoctorInRoomAsync(10);
        var (other, _) = await DoctorInRoomAsync(11);
        var visit = (await NewVisitAsync()).Visit;
        await api.CompleteVisionAsync(visit.Id);
        async Task<List<VisitRow>> QueueOf(HttpClient client) => (await client.GetFromJsonAsync<List<VisitRow>>("/api/clinical/worklist?kind=doctor", JsonOptions))!;

        await Read<CallRow>(await Call(doctor, CallDesk.Doctor, visit.Id), HttpStatusCode.Created);
        Assert.Equal(HttpStatusCode.NoContent, (await doctor.PostAsync($"/api/consultations/{visit.Id}/start", null)).StatusCode);

        // With the doctor, nothing saved yet: still on this doctor's list, and not on another doctor's.
        Assert.Contains(await QueueOf(doctor), v => v.Id == visit.Id && v.Status == nameof(VisitStatus.InConsultation));
        Assert.DoesNotContain(await QueueOf(other), v => v.Id == visit.Id);
    }

    [Fact]
    public async Task Doctor_queue_keeps_unseen_patients_and_each_seen_one_moves_once_to_the_seen_list()
    {
        var (doctor, doctorId) = await DoctorInRoomAsync(12);
        var (other, _) = await DoctorInRoomAsync(13);
        var triage = await UserAsync(Roles.Triage);
        var visits = new List<(PatientDto Patient, VisitRow Visit)>();
        for (var i = 0; i < 4; i++)
        {
            var created = await NewVisitAsync(doctorId);
            await api.CompleteVisionAsync(created.Visit.Id);
            visits.Add(created);
        }

        var ids = visits.Select(v => v.Visit.Id).ToList();
        var numbers = visits.ToDictionary(v => v.Visit.Id, v => v.Visit.QueueNumber);
        async Task<List<VisitRow>> ListOf(HttpClient client, string query) =>
            (await client.GetFromJsonAsync<List<VisitRow>>($"/api/clinical/worklist?kind=doctor{query}", JsonOptions))!.Where(v => ids.Contains(v.Id)).ToList();
        async Task<(List<VisitRow> Queue, List<VisitRow> Seen)> Lists()
        {
            var queue = await ListOf(doctor, "");
            var seen = await ListOf(doctor, "&done=true");
            // Never lost and never twice: each patient is in exactly one list, under the card number it was given.
            Assert.All(ids, id => Assert.Equal(1, queue.Count(v => v.Id == id) + seen.Count(v => v.Id == id)));
            Assert.All(queue.Concat(seen), v => Assert.Equal(numbers[v.Id], v.QueueNumber));
            // Another doctor's lists never show them.
            Assert.Empty(await ListOf(other, ""));
            Assert.Empty(await ListOf(other, "&done=true"));
            return (queue, seen);
        }

        var (one, two, three, four) = (visits[0], visits[1], visits[2], visits[3]);
        Assert.Equal(ids, (await Lists()).Queue.Select(v => v.Id).Order().ToList());

        // Opening a file, twice and without calling, takes nobody out of the queue.
        Assert.Equal(HttpStatusCode.NoContent, (await doctor.PostAsync($"/api/consultations/{three.Visit.Id}/start", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await doctor.PostAsync($"/api/consultations/{three.Visit.Id}/start", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await doctor.GetAsync($"/api/consultations/{three.Visit.Id}/chart")).StatusCode);
        Assert.Equal(4, (await Lists()).Queue.Count);

        // Called and opened with nothing saved: with the doctor, still in the queue.
        await Read<CallRow>(await Call(doctor, CallDesk.Doctor, one.Visit.Id), HttpStatusCode.Created);
        Assert.Equal(HttpStatusCode.NoContent, (await doctor.PostAsync($"/api/consultations/{one.Visit.Id}/start", null)).StatusCode);
        Assert.Contains((await Lists()).Queue, v => v.Id == one.Visit.Id && v.Status == nameof(VisitStatus.InConsultation));

        // The exam saved: seen, and the call desk no longer holds that patient.
        var exam = await Read<ConsultationDto>(await doctor.PutAsJsonAsync($"/api/consultations/{one.Visit.Id}", new SaveConsultationRequest("یافته", null, "پلان", null, null, null)));
        var lists = await Lists();
        Assert.DoesNotContain(lists.Queue, v => v.Id == one.Visit.Id);
        Assert.Contains(lists.Seen, v => v.Id == one.Visit.Id && !v.ResultReady);
        var desk = await Read<CallDeskView>(await doctor.GetAsync("/api/calls/desk/Doctor"));
        Assert.DoesNotContain(desk.Rows, r => r.VisitId == one.Visit.Id);
        Assert.Contains(desk.Rows, r => r.VisitId == two.Visit.Id);

        // Referred to refraction before any exam is written: seen at once, and still this doctor's patient.
        var tests = await doctor.GetFromJsonAsync<JsonElement>("/api/diagnostic-tests");
        var refraction = tests.EnumerateArray().First(t => t.GetProperty("kind").GetString() == "Refraction"
            && t.GetProperty("serviceId").ValueKind == JsonValueKind.Number).GetProperty("id").GetInt32();
        var order = await Read<DiagnosticOrderDto>(await doctor.SendAsync(Keyed("/api/diagnostic-orders", new CreateDiagnosticOrderRequest(two.Patient.Id, two.Visit.Id, refraction, null))), HttpStatusCode.Created);
        lists = await Lists();
        Assert.Contains(lists.Seen, v => v.Id == two.Visit.Id && v.Status == nameof(VisitStatus.AwaitingDiagnostic) && !v.ResultReady);

        await api.PayFeeAsync(await api.OrderFeeAsync(two.Patient.FileNumber, order.TestName));
        var invoicesBefore = await InDbAsync(db => db.Invoices.CountAsync(i => i.PatientId == two.Patient.Id));
        Assert.True((await triage.PutAsJsonAsync($"/api/refractions/{two.Visit.Id}", new SaveRefractionRequest("6/18", null, null, null, null, null, null, null, null, null, null))).IsSuccessStatusCode);
        Assert.True((await triage.PostAsync($"/api/refractions/{two.Visit.Id}/complete", null)).IsSuccessStatusCode);

        // Back with the result: marked on the seen list, not in the queue, not offered for a call.
        lists = await Lists();
        Assert.Contains(lists.Seen, v => v.Id == two.Visit.Id && v.Status == nameof(VisitStatus.ReadyForDoctor) && v.ResultReady);
        desk = await Read<CallDeskView>(await doctor.GetAsync("/api/calls/desk/Doctor"));
        Assert.DoesNotContain(desk.Rows, r => r.VisitId == two.Visit.Id);

        // Another doctor opening the file does not clear the mark; the patient's own doctor does.
        Assert.Equal(HttpStatusCode.NoContent, (await other.PostAsync($"/api/consultations/{two.Visit.Id}/start", null)).StatusCode);
        Assert.Contains((await Lists()).Seen, v => v.Id == two.Visit.Id && v.ResultReady);
        Assert.Equal(HttpStatusCode.NoContent, (await doctor.PostAsync($"/api/consultations/{two.Visit.Id}/start", null)).StatusCode);
        Assert.Contains((await Lists()).Seen, v => v.Id == two.Visit.Id && !v.ResultReady && v.Status == nameof(VisitStatus.InConsultation));

        // No new visit, card, call or fee came from the round trip.
        Assert.Equal(1, await InDbAsync(db => db.Visits.CountAsync(v => v.PatientId == two.Patient.Id)));
        Assert.Equal(invoicesBefore, await InDbAsync(db => db.Invoices.CountAsync(i => i.PatientId == two.Patient.Id)));
        Assert.Equal(0, await InDbAsync(db => db.PatientCalls.CountAsync(c => c.VisitId == two.Visit.Id)));

        // Closing the first visit keeps it on the seen list; the two never seen are still waiting in card order.
        Assert.Equal(HttpStatusCode.OK, (await doctor.PostAsJsonAsync($"/api/consultations/{one.Visit.Id}/finalize", new FinalizeRequest(exam.Version, true))).StatusCode);
        lists = await Lists();
        Assert.Contains(lists.Seen, v => v.Id == one.Visit.Id && v.Status == nameof(VisitStatus.Completed));
        Assert.Equal(new[] { three.Visit.Id, four.Visit.Id }, lists.Queue.Select(v => v.Id).Order().ToArray());
        Assert.Equal(new[] { one.Visit.Id, two.Visit.Id }, lists.Seen.Select(v => v.Id).Order().ToArray());
    }

    [Fact]
    public async Task Concurrent_calls_cannot_reserve_two_different_patients_on_one_desk()
    {
        var (doctor, doctorId) = await DoctorInRoomAsync(8);
        var one = (await NewVisitAsync(doctorId)).Visit;
        var two = (await NewVisitAsync(doctorId)).Visit;
        await api.CompleteVisionAsync(one.Id); await api.CompleteVisionAsync(two.Id);
        var responses = await Task.WhenAll(Call(doctor, CallDesk.Doctor, one.Id), Call(doctor, CallDesk.Doctor, two.Id));
        Assert.Equal(HttpStatusCode.Created, responses[0].StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, responses[1].StatusCode);
        var lane = await InDbAsync(db => db.QueueDeskLanes.SingleAsync(l => l.Day == KabulClock.Today && l.Desk == CallDesk.Doctor));
        Assert.Equal($"v:{one.Id}", lane.EntryKey);
    }
    [Fact]
    public async Task Tv_doctor_queue_uses_consultation_state_and_keeps_next_patient_after_audio_finishes()
    {
        var (_, current) = await NewVisitAsync();
        var (_, next) = await NewVisitAsync();
        await InDbAsync(async db =>
        {
            var doctor = new Staff { FullName = Name("doctor"), IsDoctor = true };
            db.Staff.Add(doctor);
            await db.SaveChangesAsync();
            await db.Visits.Where(v => v.Id == current.Id).ExecuteUpdateAsync(s => s.SetProperty(v => v.Status, VisitStatus.InConsultation).SetProperty(v => v.DoctorStaffId, doctor.Id));
            await db.Visits.Where(v => v.Id == next.Id).ExecuteUpdateAsync(s => s.SetProperty(v => v.Status, VisitStatus.ReadyForDoctor).SetProperty(v => v.DoctorStaffId, doctor.Id));
            db.PatientCalls.Add(new PatientCall { VisitId = current.Id, Desk = CallDesk.Doctor, QueueNumber = current.QueueNumber!.Value,
                QueueShift = current.QueueShift, QueueRound = current.QueueRound, Destination = VoicePack.Room(1),
                RequestedByUserId = await db.Users.Select(u => u.Id).FirstAsync(), Status = CallStatus.Played,
                RequestedAt = DateTime.UtcNow.AddMinutes(-5), FinishedAt = DateTime.UtcNow.AddMinutes(-4) });
            return await db.SaveChangesAsync();
        });
        var tv = await UserAsync(Roles.AudioStation);
        var response = await tv.GetAsync("/api/calls/display/doctor-queue");
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(current.QueueNumber, json.GetProperty("currentNumber").GetInt32());
        Assert.Equal(next.QueueNumber, json.GetProperty("nextNumber").GetInt32());
        Assert.DoesNotContain("fileNumber", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };
    private static readonly string VoiceRoot = Path.Combine(Path.GetTempPath(), "alshifa-test-storage", "voice");

    private static string Name(string prefix) => $"{prefix}_{Guid.NewGuid():N}"[..20];

    private async Task<HttpClient> UserAsync(params string[] roles)
    {
        var name = Name("u");
        await api.CreateUserAsync(name, roles);
        return await api.LoginAsync(name);
    }

    private static HttpRequestMessage Keyed(string url, object body) => ApiFixture.Keyed(url, body);

    private static async Task<T> Read<T>(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, text);
        return JsonSerializer.Deserialize<T>(text, JsonOptions)!;
    }

    private static async Task AssertRefused(HttpResponseMessage response, string code, HttpStatusCode status = HttpStatusCode.Conflict)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == status, text);
        Assert.Contains(code, text);
    }

    private async Task<T> InDbAsync<T>(Func<AppDbContext, Task<T>> action)
    {
        using var scope = api.Services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    /// <summary>A clean speaker for each test: an active station, nothing left in the air and every clip present.</summary>
    private async Task<(HttpClient Client, string StationId)> StationAsync()
    {
        Directory.CreateDirectory(VoiceRoot);
        foreach (var clip in VoicePack.AllClips())
        {
            var path = Path.Combine(VoiceRoot, clip);
            if (!File.Exists(path))
            {
                await File.WriteAllBytesAsync(path, [0xFF, 0xFB]);
            }
        }

        await InDbAsync(db => db.PatientCalls
            .Where(c => c.Status == CallStatus.Queued || c.Status == CallStatus.Playing)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.Status, CallStatus.Cancelled)));

        var client = await UserAsync(Roles.AudioStation);
        var id = Guid.NewGuid().ToString("N");
        var claimed = await client.PostAsJsonAsync("/api/calls/station/claim", new ClaimStationRequest(id, true));
        Assert.True(claimed.IsSuccessStatusCode, await claimed.Content.ReadAsStringAsync());
        return (client, id);
    }

    private static async Task<NextCallDto?> NextAsync(HttpClient station, string id)
    {
        var response = await station.PostAsJsonAsync("/api/calls/station/next", new NextCallRequest(id));
        if (response.StatusCode == HttpStatusCode.NoContent)
        {
            return null;
        }

        return await Read<NextCallDto>(response);
    }

    private static Task<HttpResponseMessage> Finish(HttpClient station, string id, long callId, bool played = true) =>
        station.PostAsJsonAsync($"/api/calls/{callId}/result", new CallResultRequest(id, played, played ? null : "خطا"));

    private async Task<(PatientDto Patient, VisitRow Visit)> NewVisitAsync(long? doctorStaffId = null, string department = "triage")
    {
        var reception = await api.FeeDeskAsync();
        var patient = await Read<PatientDto>(await reception.SendAsync(Keyed("/api/patients",
            new SavePatientRequest(Name("احمد"), null, "محمود", Gender.Male, null, 30, null, null))), HttpStatusCode.Created);
        var visit = await Read<VisitRow>(await reception.SendAsync(Keyed("/api/visits",
            new CreateVisitRequest(patient.Id, await api.DepartmentIdAsync(department), doctorStaffId, null, null, await api.PaidTokenAsync(department)))), HttpStatusCode.Created);
        return (patient, visit);
    }

    private async Task<(HttpClient Client, long StaffId)> DoctorInRoomAsync(int room)
    {
        var name = Name("doc");
        var userId = await api.CreateUserAsync(name, Roles.Doctor);
        var client = await api.LoginAsync(name);
        var staffId = await InDbAsync(async db =>
        {
            var staff = new Staff { FullName = name, IsDoctor = true, UserId = userId };
            db.Staff.Add(staff);
            await db.SaveChangesAsync();
            return staff.Id;
        });

        var admin = await UserAsync(Roles.SuperAdmin);
        var saved = await admin.PutAsJsonAsync("/api/calls/settings", new SaveCallSettingsRequest([], [new CallDoctorRoom(staffId, room)]));
        Assert.True(saved.IsSuccessStatusCode, await saved.Content.ReadAsStringAsync());
        return (client, staffId);
    }

    private static Task<HttpResponseMessage> Call(HttpClient client, CallDesk desk, long visitId, bool recall = false) =>
        client.PostAsJsonAsync("/api/calls", new CreateCallRequest(desk, visitId, null, recall));

    [Fact]
    public void Tv_display_label_keeps_the_actual_section_when_destination_is_a_room()
    {
        Assert.Equal("بخش داکتر — اتاق شماره 3", VoicePack.DisplayLabel(CallDesk.Doctor, VoicePack.Room(3)));
        Assert.Equal("بخش ویژن — اتاق شماره 7", VoicePack.DisplayLabel(CallDesk.Vision, VoicePack.Room(7)));
    }

    [Fact]
    public async Task Tv_observes_number_without_claiming_audio_and_keeps_finished_calls_for_today()
    {
        var (station, stationId) = await StationAsync();
        var (patient, visit) = await NewVisitAsync();
        var triage = await UserAsync(Roles.Triage);
        var tv = await UserAsync(Roles.AudioStation);
        await InDbAsync(db => db.PatientCalls.Where(c => c.Status == CallStatus.Played).ExecuteUpdateAsync(s => s.SetProperty(c => c.RequestedAt, DateTime.UtcNow.AddDays(-2))));
        await Read<CallRow>(await Call(triage, CallDesk.Vision, visit.Id), HttpStatusCode.Created);
        Assert.Equal(HttpStatusCode.NoContent, (await tv.GetAsync("/api/calls/display")).StatusCode);
        var next = (await NextAsync(station, stationId))!;
        var displayResponse = await tv.GetAsync("/api/calls/display");
        var displayText = await displayResponse.Content.ReadAsStringAsync();
        Assert.DoesNotContain("patientName", displayText, StringComparison.OrdinalIgnoreCase);
        var display = JsonSerializer.Deserialize<DisplayCallDto>(displayText, JsonOptions)!;
        Assert.Equal(next.Id, display.Id);
        Assert.Equal(visit.QueueNumber, display.QueueNumber);
        Assert.Contains("بخش ویژن", display.Destination);
        Assert.Equal(HttpStatusCode.Forbidden, (await triage.GetAsync("/api/calls/display")).StatusCode);
        Assert.True((await Finish(station, stationId, next.Id)).IsSuccessStatusCode);
        Assert.Equal(next.Id, (await Read<DisplayCallDto>(await tv.GetAsync("/api/calls/display"))).Id);
        await InDbAsync(db => db.PatientCalls.Where(c => c.Id == next.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.FinishedAt, DateTime.UtcNow.AddMinutes(-1))));
        Assert.Equal(next.Id, (await Read<DisplayCallDto>(await tv.GetAsync("/api/calls/display"))).Id);
    }

    [Fact]
    public async Task A_call_reaches_the_speaker_once_with_number_and_place_only_and_changes_nothing_else()
    {
        var (station, stationId) = await StationAsync();
        var (patient, visit) = await NewVisitAsync();
        var triage = await UserAsync(Roles.Triage);
        var invoicesBefore = await InDbAsync(db => db.Invoices.CountAsync());
        var visitsBefore = await InDbAsync(db => db.Visits.CountAsync());

        var call = await Read<CallRow>(await Call(triage, CallDesk.Vision, visit.Id), HttpStatusCode.Created);
        Assert.Equal("Queued", call.Status);

        var raw = await station.PostAsJsonAsync("/api/calls/station/next", new NextCallRequest(stationId));
        var text = await raw.Content.ReadAsStringAsync();
        Assert.DoesNotContain(patient.FirstName, text);
        Assert.DoesNotContain("fileNumber", text, StringComparison.OrdinalIgnoreCase);
        var next = JsonSerializer.Deserialize<NextCallDto>(text, JsonOptions)!;
        Assert.Equal(VoicePack.RecordedClips(visit.QueueNumber!.Value, "vision", CallDesk.Vision), next.Clips);

        // Nothing else is handed out while one is playing, so announcements cannot overlap.
        await AssertRefused(await Call(triage, CallDesk.Vision, visit.Id), "already_calling");
        Assert.Null(await NextAsync(station, stationId));

        Assert.True((await Finish(station, stationId, next.Id)).IsSuccessStatusCode);
        Assert.Null(await NextAsync(station, stationId));

        // No medical or financial state moved, and a call opens neither a visit nor an invoice.
        var after = await InDbAsync(db => db.Visits.AsNoTracking().SingleAsync(v => v.Id == visit.Id));
        Assert.Equal((visit.Status, visit.Version), (after.Status.ToString(), after.Version));
        Assert.Equal(invoicesBefore, await InDbAsync(db => db.Invoices.CountAsync()));
        Assert.Equal(visitsBefore, await InDbAsync(db => db.Visits.CountAsync()));

        var view = await Read<CallDeskView>(await triage.GetAsync("/api/calls/desk/Vision"));
        Assert.Equal("Played", view.Rows.Single(r => r.VisitId == visit.Id).LastCall!.Status);

        // Recall has a new event ID and repeats the recorded announcement; history retains the actor.
        await Read<CallRow>(await Call(triage, CallDesk.Vision, visit.Id, recall: true), HttpStatusCode.Created);
        Assert.Equal(VoicePack.RecordedBase + "destinations/destination_initial_exam.wav", (await NextAsync(station, stationId))!.Clips[1]);
        var audit = await InDbAsync(db => db.AuditLogs.AsNoTracking().Where(a => a.Action == "PatientRecalled" && a.EntityId == visit.Id.ToString()).SingleAsync());
        Assert.Contains(visit.VisitNumber, audit.Details);
        Assert.NotNull(audit.UserId);
    }

    [Fact]
    public async Task A_transfer_only_enters_the_queue_and_never_calls_by_itself()
    {
        var (station, stationId) = await StationAsync();
        var (doctor, staffId) = await DoctorInRoomAsync(3);
        var (_, visit) = await NewVisitAsync(staffId);
        var triage = await UserAsync(Roles.Triage);

        await AssertRefused(await Call(doctor, CallDesk.Doctor, visit.Id), "not_in_queue");

        await api.CompleteVisionAsync(visit.Id);
        Assert.Null(await NextAsync(station, stationId));
        Assert.Equal(0, await InDbAsync(db => db.PatientCalls.CountAsync(c => c.VisitId == visit.Id)));

        await AssertRefused(await Call(triage, CallDesk.Vision, visit.Id), "not_in_queue");
        await Read<CallRow>(await Call(doctor, CallDesk.Doctor, visit.Id), HttpStatusCode.Created);
        Assert.Equal(VoicePack.RecordedBase + "destinations/destination_doctor.wav", (await NextAsync(station, stationId))!.Clips[1]);
    }

    [Fact]
    public async Task Paid_desks_wait_for_valid_payment_and_the_patient_returns_to_the_same_doctors_seen_list()
    {
        var (station, stationId) = await StationAsync();
        var (doctor, staffId) = await DoctorInRoomAsync(4);
        var (patient, visit) = await NewVisitAsync(staffId);
        await api.CompleteVisionAsync(visit.Id);
        var triage = await UserAsync(Roles.Triage);

        var tests = await doctor.GetFromJsonAsync<JsonElement>("/api/diagnostic-tests");
        var testId = tests.EnumerateArray().First(t => t.GetProperty("kind").GetString() == "Refraction" && t.GetProperty("serviceId").ValueKind == JsonValueKind.Number).GetProperty("id").GetInt32();
        var order = await Read<DiagnosticOrderDto>(await doctor.SendAsync(Keyed("/api/diagnostic-orders", new CreateDiagnosticOrderRequest(patient.Id, visit.Id, testId, null))), HttpStatusCode.Created);

        await AssertRefused(await Call(triage, CallDesk.Refraction, visit.Id), "not_in_queue");
        await AssertRefused(await Call(doctor, CallDesk.Doctor, visit.Id), "not_in_queue");

        await api.PayFeeAsync(await api.OrderFeeAsync(patient.FileNumber, order.TestName));

        var called = await Read<CallRow>(await Call(triage, CallDesk.Refraction, visit.Id), HttpStatusCode.Created);
        var next = await NextAsync(station, stationId);
        Assert.Equal(called.Id, next!.Id);
        Assert.Equal(VoicePack.RecordedBase + "destinations/destination_refraction.wav", next.Clips[1]);
        Assert.True((await Finish(station, stationId, next.Id)).IsSuccessStatusCode);

        // The doctor still waits for the answer, so the patient cannot be called back yet.
        await AssertRefused(await Call(doctor, CallDesk.Doctor, visit.Id), "not_in_queue");

        var saved = await triage.PutAsJsonAsync($"/api/refractions/{visit.Id}", new SaveRefractionRequest("6/18", null, null, null, null, null, null, null, null, null, null));
        Assert.True(saved.IsSuccessStatusCode, await saved.Content.ReadAsStringAsync());
        Assert.True((await triage.PostAsync($"/api/refractions/{visit.Id}/complete", null)).IsSuccessStatusCode);

        await AssertRefused(await Call(triage, CallDesk.Refraction, visit.Id), "not_in_queue");
        // Back with the result the patient is neither queued nor called again: the doctor finds the result marked on the seen list.
        await AssertRefused(await Call(doctor, CallDesk.Doctor, visit.Id), "not_in_queue");
        Assert.Null(await NextAsync(station, stationId));
        var seen = (await doctor.GetFromJsonAsync<List<VisitRow>>("/api/clinical/worklist?kind=doctor&done=true", JsonOptions))!;
        Assert.Contains(seen, v => v.Id == visit.Id && v.ResultReady);
        var other = await DoctorInRoomAsync(5);
        Assert.DoesNotContain((await other.Client.GetFromJsonAsync<List<VisitRow>>("/api/clinical/worklist?kind=doctor&done=true", JsonOptions))!, v => v.Id == visit.Id);
    }

    [Fact]
    public async Task Only_the_desk_may_call_and_only_the_station_may_play()
    {
        var (station, stationId) = await StationAsync();
        var (_, visit) = await NewVisitAsync();
        var pharmacist = await UserAsync(Roles.Pharmacist);
        var triage = await UserAsync(Roles.Triage);
        var manager = await UserAsync(Roles.Manager);

        await AssertRefused(await Call(pharmacist, CallDesk.Vision, visit.Id), "", HttpStatusCode.Forbidden);
        await AssertRefused(await Call(manager, CallDesk.Vision, visit.Id), "", HttpStatusCode.Forbidden);
        await AssertRefused(await Call(station, CallDesk.Vision, visit.Id), "", HttpStatusCode.Forbidden);
        Assert.Equal(HttpStatusCode.Forbidden, (await pharmacist.GetAsync("/api/calls/desk/Vision")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await station.GetAsync("/api/calls/desk/Vision")).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await triage.PostAsJsonAsync("/api/calls/station/next", new NextCallRequest(stationId))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await triage.PostAsJsonAsync("/api/calls/station/claim", new ClaimStationRequest("x", true))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await triage.GetAsync("/api/calls/voice/to-vision.mp3")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await (await UserAsync(Roles.Accountant)).GetAsync("/api/calls/voice/to-vision.mp3")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.NewClient().GetAsync("/api/calls/voice/to-vision.mp3")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await station.GetAsync("/api/calls/voice/..%2F..%2Fsecret.mp3")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await station.GetAsync("/api/calls/voice/to-vision.mp3")).StatusCode);

        // The station's history shows numbers and staff, never who the patient is.
        var status = await station.GetStringAsync($"/api/calls/station/status?stationId={stationId}");
        Assert.DoesNotContain(visit.PatientName, status);
    }

    [Fact]
    public async Task Simultaneous_presses_call_a_patient_once_and_different_patients_play_one_by_one_in_order()
    {
        var (station, stationId) = await StationAsync();
        var triage = await UserAsync(Roles.Triage);
        var (_, one) = await NewVisitAsync();

        var presses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Call(triage, CallDesk.Vision, one.Id)));
        Assert.Single(presses, p => p.StatusCode == HttpStatusCode.Created);
        Assert.All(presses.Where(p => p.StatusCode != HttpStatusCode.Created), p => Assert.Equal(HttpStatusCode.Conflict, p.StatusCode));
        Assert.Equal(1, await InDbAsync(db => db.PatientCalls.CountAsync(c => c.VisitId == one.Id)));

        var others = new List<VisitRow>();
        for (var i = 0; i < 5; i++)
        {
            others.Add((await NewVisitAsync()).Visit);
        }

        var played = new List<long>();
        var firstAudio = (await NextAsync(station, stationId))!;
        played.Add(firstAudio.Id);
        Assert.Null(await NextAsync(station, stationId));
        Assert.True((await Finish(station, stationId, firstAudio.Id)).IsSuccessStatusCode);
        await api.CompleteVisionAsync(one.Id);
        foreach (var visit in others)
        {
            await Read<CallRow>(await Call(triage, CallDesk.Vision, visit.Id), HttpStatusCode.Created);
            var next = (await NextAsync(station, stationId))!;
            played.Add(next.Id);
            Assert.Null(await NextAsync(station, stationId));
            Assert.True((await Finish(station, stationId, next.Id)).IsSuccessStatusCode);
            await api.CompleteVisionAsync(visit.Id);
        }

        Assert.Equal(6, played.Count);
        Assert.Equal(played.Order(), played);
        Assert.Equal(played.Count, played.Distinct().Count());
    }

    [Fact]
    public async Task After_an_outage_nothing_is_repeated_and_old_calls_stay_silent_until_someone_recalls()
    {
        var (station, stationId) = await StationAsync();
        var triage = await UserAsync(Roles.Triage);
        var (_, claimed) = await NewVisitAsync();
        var (_, waiting) = await NewVisitAsync();

        // The station took this one and lost the network before it could report back.
        var first = await Read<CallRow>(await Call(triage, CallDesk.Vision, claimed.Id), HttpStatusCode.Created);
        Assert.Equal(first.Id, (await NextAsync(station, stationId))!.Id);
        await Read<CallRow>(await triage.PostAsJsonAsync($"/api/calls/{first.Id}/outcome", new CallOutcomeRequest(CallOutcome.NoShow)));
        var second = await Read<CallRow>(await Call(triage, CallDesk.Vision, waiting.Id), HttpStatusCode.Created);

        var longAgo = DateTime.UtcNow.AddMinutes(-10);
        await InDbAsync(db => db.PatientCalls.Where(c => c.Id == first.Id || c.Id == second.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.RequestedAt, longAgo).SetProperty(c => c.ClaimedAt, c => c.ClaimedAt == null ? null : longAgo)));

        // Staff see the failure at once, even before the station is back.
        var view = await Read<CallDeskView>(await triage.GetAsync("/api/calls/desk/Vision"));
        Assert.Equal("Failed", view.Rows.Single(r => r.VisitId == waiting.Id).LastCall!.Status);

        Assert.Null(await NextAsync(station, stationId));
        Assert.Null(await NextAsync(station, stationId));
        Assert.Equal(CallStatus.Failed, await InDbAsync(db => db.PatientCalls.Where(c => c.Id == first.Id).Select(c => c.Status).SingleAsync()));
        Assert.Equal(CallStatus.Failed, await InDbAsync(db => db.PatientCalls.Where(c => c.Id == second.Id).Select(c => c.Status).SingleAsync()));

        // A late report from the old attempt cannot revive it, and a person's recall works normally.
        Assert.True((await Finish(station, stationId, first.Id)).IsSuccessStatusCode);
        Assert.Equal(CallStatus.Failed, await InDbAsync(db => db.PatientCalls.Where(c => c.Id == first.Id).Select(c => c.Status).SingleAsync()));
        var recall = await Read<CallRow>(await Call(triage, CallDesk.Vision, waiting.Id, recall: true), HttpStatusCode.Created);
        Assert.Equal(recall.Id, (await NextAsync(station, stationId))!.Id);
    }

    [Fact]
    public async Task One_station_at_a_time_and_missing_legacy_audio_does_not_block_calls()
    {
        var (first, firstId) = await StationAsync();
        var triage = await UserAsync(Roles.Triage);
        var (_, visit) = await NewVisitAsync();

        var secondId = Guid.NewGuid().ToString("N");
        var second = await UserAsync(Roles.AudioStation);
        await AssertRefused(await second.PostAsJsonAsync("/api/calls/station/claim", new ClaimStationRequest(secondId, false)), "station_busy");
        Assert.Null(await NextAsync(first, firstId));
        await AssertRefused(await second.PostAsJsonAsync("/api/calls/station/next", new NextCallRequest(secondId)), "station_replaced");

        var takeOver = await second.PostAsJsonAsync("/api/calls/station/claim", new ClaimStationRequest(secondId, true));
        Assert.True(takeOver.IsSuccessStatusCode);
        await AssertRefused(await first.PostAsJsonAsync("/api/calls/station/next", new NextCallRequest(firstId)), "station_replaced");

        var clip = Path.Combine(VoiceRoot, "to-vision.mp3");
        File.Delete(clip);
        try
        {
            var created = await Read<CallRow>(await Call(triage, CallDesk.Vision, visit.Id), HttpStatusCode.Created);
            Assert.Equal(created.Id, (await NextAsync(second, secondId))!.Id);
            Assert.True((await Finish(second, secondId, created.Id)).IsSuccessStatusCode);
            var status = await Read<StationStatus>(await second.GetAsync($"/api/calls/station/status?stationId={secondId}"));
            Assert.True(status.Active);
            Assert.Equal(0, status.ClipsMissing);
            Assert.Equal(HttpStatusCode.NotFound, (await second.GetAsync("/api/calls/voice/to-vision.mp3")).StatusCode);
        }
        finally
        {
            await File.WriteAllBytesAsync(clip, [0xFF, 0xFB]);
        }

        var stale = DateTime.UtcNow.AddMinutes(-10).Ticks;
        await InDbAsync(db => db.Settings.Where(s => s.Key == "calls.station").ExecuteUpdateAsync(s => s.SetProperty(x => x.Value, $"{secondId}|{stale}")));
        Assert.False((await Read<CallDeskView>(await triage.GetAsync("/api/calls/desk/Vision"))).StationOnline);

        // No station online: the desk that pressed the button announces it, and a station returning later does not repeat it.
        var local = await Read<CallRow>(await Call(triage, CallDesk.Vision, visit.Id, recall: true), HttpStatusCode.Created);
        Assert.Equal(VoicePack.RecordedClips(visit.QueueNumber!.Value, "vision", CallDesk.Vision), local.LocalClips);
        Assert.Equal(nameof(CallStatus.Playing), local.Status);
        await AssertRefused(await Call(triage, CallDesk.Vision, visit.Id), "already_calling");

        var colleague = await UserAsync(Roles.Triage);
        Assert.Equal(HttpStatusCode.Forbidden, (await colleague.PostAsJsonAsync($"/api/calls/{local.Id}/local-result", new LocalResultRequest(true, null))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await triage.PostAsJsonAsync($"/api/calls/{local.Id}/local-result", new LocalResultRequest(true, null))).StatusCode);
        var row = (await Read<CallDeskView>(await triage.GetAsync("/api/calls/desk/Vision"))).Rows.Single(r => r.VisitId == visit.Id);
        Assert.Equal(nameof(CallStatus.Played), row.LastCall!.Status);

        Assert.True((await second.PostAsJsonAsync("/api/calls/station/claim", new ClaimStationRequest(secondId, true))).IsSuccessStatusCode);
        Assert.Null(await NextAsync(second, secondId));
    }

    [Fact]
    public async Task Attendance_is_recorded_without_moving_the_patient_and_a_waiting_announcement_is_withdrawn()
    {
        var (station, stationId) = await StationAsync();
        var triage = await UserAsync(Roles.Triage);
        var (_, visit) = await NewVisitAsync();

        await AssertRefused(await Call(triage, CallDesk.Vision, visit.Id, recall: true), "no_previous_call");
        var call = await Read<CallRow>(await Call(triage, CallDesk.Vision, visit.Id), HttpStatusCode.Created);

        var pharmacist = await UserAsync(Roles.Pharmacist);
        Assert.Equal(HttpStatusCode.Forbidden, (await pharmacist.PostAsJsonAsync($"/api/calls/{call.Id}/outcome", new CallOutcomeRequest(CallOutcome.Attended))).StatusCode);

        var attended = await Read<CallRow>(await triage.PostAsJsonAsync($"/api/calls/{call.Id}/outcome", new CallOutcomeRequest(CallOutcome.Attended)));
        Assert.Equal(("Cancelled", "Attended"), (attended.Status, attended.Outcome));
        Assert.Null(await NextAsync(station, stationId));

        var after = await InDbAsync(db => db.Visits.AsNoTracking().SingleAsync(v => v.Id == visit.Id));
        Assert.Equal((visit.Status, visit.Version), (after.Status.ToString(), after.Version));

        var again = await Read<CallRow>(await Call(triage, CallDesk.Vision, visit.Id, recall: true), HttpStatusCode.Created);
        var noShow = await Read<CallRow>(await triage.PostAsJsonAsync($"/api/calls/{again.Id}/outcome", new CallOutcomeRequest(CallOutcome.NoShow)));
        Assert.Equal("NoShow", noShow.Outcome);
    }

    [Fact]
    public async Task Reception_calls_an_unregistered_token_and_rooms_come_from_settings()
    {
        var (station, stationId) = await StationAsync();
        var desk = await api.FeeDeskAsync();
        var unpaid = await api.NewTokenAsync();
        var tokenId = await api.PaidTokenAsync();

        var view = await Read<CallDeskView>(await desk.GetAsync("/api/calls/desk/Reception"));
        Assert.Contains(view.Rows, r => r.TokenId == tokenId && r.QueueNumber > 0);
        Assert.DoesNotContain(view.Rows, r => r.TokenId == unpaid.Id);

        await Read<CallRow>(await desk.PostAsJsonAsync("/api/calls", new CreateCallRequest(CallDesk.Reception, null, tokenId, false)), HttpStatusCode.Created);
        Assert.Equal(VoicePack.RecordedBase + "destinations/destination_reception.wav", (await NextAsync(station, stationId))!.Clips[1]);

        var admin = await UserAsync(Roles.SuperAdmin);
        await AssertRefused(await admin.PutAsJsonAsync("/api/calls/settings", new SaveCallSettingsRequest([new CallDeskRoom(CallDesk.Vision, 99)], [])), "invalid_room", HttpStatusCode.BadRequest);
        Assert.True((await admin.PutAsJsonAsync("/api/calls/settings", new SaveCallSettingsRequest([new CallDeskRoom(CallDesk.Vision, 7)], []))).IsSuccessStatusCode);
        try
        {
            var (_, visit) = await NewVisitAsync();
            var triage = await UserAsync(Roles.Triage);
            var stationClear = await NextAsync(station, stationId);
            if (stationClear is not null)
            {
                await Finish(station, stationId, stationClear.Id);
            }

            await InDbAsync(db => db.PatientCalls.Where(c => c.Status == CallStatus.Playing).ExecuteUpdateAsync(s => s.SetProperty(c => c.Status, CallStatus.Played)));
            await Read<CallRow>(await Call(triage, CallDesk.Vision, visit.Id), HttpStatusCode.Created);
            Assert.Equal(VoicePack.RecordedBase + "destinations/destination_initial_exam.wav", (await NextAsync(station, stationId))!.Clips[1]);
        }
        finally
        {
            await admin.PutAsJsonAsync("/api/calls/settings", new SaveCallSettingsRequest([new CallDeskRoom(CallDesk.Vision, null)], []));
        }
    }

    [Fact]
    public async Task Cashier_calls_visits_waiting_for_payment_with_cashier_audio()
    {
        var (station, stationId) = await StationAsync();
        var reception = await api.FeeDeskAsync();
        var patient = await Read<PatientDto>(await reception.SendAsync(Keyed("/api/patients",
            new SavePatientRequest(Name("بیمار"), null, null, Gender.Male, null, 30, null, null))), HttpStatusCode.Created);
        var admission = await Read<AdmissionRow>(await reception.SendAsync(Keyed("/api/admissions",
            new CreateAdmissionRequest(patient.Id, await api.DepartmentIdAsync("triage"), null, "تاری دید", QueueNumber: api.NextPhysicalCard()))), HttpStatusCode.Created);
        var cashier = await api.FeeCashierAsync();

        var desk = await Read<CallDeskView>(await cashier.GetAsync("/api/calls/desk/Cashier"));
        Assert.Contains(desk.Rows, row => row.VisitId == admission.VisitId && row.QueueNumber == admission.QueueNumber);

        await Read<CallRow>(await Call(cashier, CallDesk.Cashier, admission.VisitId), HttpStatusCode.Created);
        Assert.Equal(VoicePack.RecordedBase + "destinations/destination_cashier.wav", (await NextAsync(station, stationId))!.Clips[1]);

        var triage = await UserAsync(Roles.Triage);
        Assert.Equal(HttpStatusCode.Forbidden, (await triage.GetAsync("/api/calls/desk/Cashier")).StatusCode);
    }

    [Fact]
    public async Task A_visit_keeps_one_number_and_no_two_patients_share_a_number_across_departments()
    {
        var triageToken = await api.NewTokenAsync("triage");
        var doctorToken = await api.NewTokenAsync("doctor");
        Assert.NotNull(doctorToken.QueueNumber);

        // The department changes at registration; the number on the patient's slip does not.
        var desk = await api.FeeDeskAsync();
        var patient = await Read<PatientDto>(await desk.SendAsync(Keyed("/api/patients", new SavePatientRequest(Name("احمد"), null, "محمود", Gender.Male, null, 30, null, null))), HttpStatusCode.Created);
        var cashier = await api.FeeCashierAsync();
        var paid = await cashier.SendAsync(Keyed("/api/payments", new PaymentRequest(doctorToken.InvoiceId, doctorToken.Balance, PaymentMethod.Cash, null, doctorToken.InvoiceVersion)));
        Assert.True(paid.IsSuccessStatusCode);
        var paidOther = await cashier.SendAsync(Keyed("/api/payments", new PaymentRequest(triageToken.InvoiceId, triageToken.Balance, PaymentMethod.Cash, null, triageToken.InvoiceVersion)));
        Assert.True(paidOther.IsSuccessStatusCode);
        var number = (await Read<VisitTokenRow>(await desk.GetAsync($"/api/visit-tokens/{doctorToken.Id}"))).QueueNumber;
        Assert.NotEqual((await Read<VisitTokenRow>(await desk.GetAsync($"/api/visit-tokens/{triageToken.Id}"))).QueueNumber, number);
        var visit = await Read<VisitRow>(await desk.SendAsync(Keyed("/api/visits", new CreateVisitRequest(patient.Id, await api.DepartmentIdAsync("triage"), null, null, null, doctorToken.Id))), HttpStatusCode.Created);
        Assert.Equal(number, visit.QueueNumber);
    }

    [Fact]
    public void The_pack_tool_produces_exactly_the_places_the_api_announces()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(dir!.FullName, "deploy", "windows", "voice-phrases.json")))
        {
            dir = dir.Parent;
        }

        using var phrases = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir.FullName, "deploy", "windows", "voice-phrases.json")));
        var root = phrases.RootElement;
        Assert.Equal(VoicePack.MaxNumber, root.GetProperty("maxNumber").GetInt32());
        Assert.Equal(VoicePack.MaxRoom, root.GetProperty("maxRoom").GetInt32());
        Assert.Equal(
            VoicePack.Sections.Values.ToDictionary(s => s.Clip, s => s.Label).OrderBy(s => s.Key),
            root.GetProperty("sections").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!).OrderBy(s => s.Key));
    }

    [Fact]
    public async Task The_station_gets_a_live_signal_without_patient_data()
    {
        var (_, stationId) = await StationAsync();
        var name = Name("st");
        await api.CreateUserAsync(name, Roles.AudioStation);
        var baseUri = new Uri("https://localhost");
        var cookies = new CookieContainerHandler();
        var client = api.CreateDefaultClient(baseUri, cookies);
        await ApiFixture.RefreshCsrfAsync(client);
        Assert.True((await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(name, ApiFixture.TestPassword))).IsSuccessStatusCode);

        var events = new ConcurrentQueue<(string Type, string Payload)>();
        await using var hub = new HubConnectionBuilder()
            .WithUrl(new Uri(baseUri, "/hubs/events"), HttpTransportType.LongPolling, options =>
            {
                options.HttpMessageHandlerFactory = _ => api.Server.CreateHandler();
                options.Headers["Cookie"] = cookies.Container.GetCookieHeader(baseUri);
            })
            .Build();
        hub.On<JsonElement>("event", e => events.Enqueue((e.GetProperty("type").GetString()!, e.GetProperty("payload").GetString()!)));
        await hub.StartAsync();

        var (patient, visit) = await NewVisitAsync();
        var triage = await UserAsync(Roles.Triage);
        await Read<CallRow>(await Call(triage, CallDesk.Vision, visit.Id), HttpStatusCode.Created);

        var until = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < until && !events.Any(e => e.Type == "PatientCallRequested"))
        {
            await Task.Delay(100);
        }

        var signal = Assert.Single(events, e => e.Type == "PatientCallRequested");
        Assert.DoesNotContain(patient.FirstName, signal.Payload);
        Assert.DoesNotContain(visit.Id.ToString(), signal.Payload);
        Assert.NotEmpty(stationId);
    }
}
